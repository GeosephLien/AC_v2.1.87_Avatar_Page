using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine.Events;

[System.Serializable]
public class ZoomStepData
{
    public float zoomZ;
    public Transform pivotFollow;

    [Header("Pivot Follow Offset")]
    public Vector3 pivotOffset;

    [Header("Optional UI Buttons")]
    public List<Button> triggerButtons = new List<Button>(); // ✅ 改成 List
}

public class MouseCameraController : MonoBehaviour
{
    [Header("Target Transforms")]
    public Transform targetRotY;
    public Transform targetRotX;
    public Transform targetZoom;

    [Header("Initial Values")]
    public float initialRotY = 0f;
    public float initialRotX = 0f;

    [Header("Pivot Root (Y follows pivotFollow)")]
    public Transform pivotRoot;

    [Header("Rotation Settings")]
    public float rotationSpeedY = 3.0f;
    public float rotationSpeedX = 3.0f;
    public float brakeY = 8.0f;
    public float brakeX = 8.0f;

    [Header("X Axis Rotation Limit")]
    public float minX = -60f;
    public float maxX = 60f;

    [Header("Zoom Settings")]
    [Tooltip("Place a component that has a Target Character reference and raises onTargetCharacterChanged.")]
    public MonoBehaviour targetCharacterSource;

    public List<ZoomStepData> zoomSteps = new List<ZoomStepData>();
    public float zoomBrake = 10f;

    [Tooltip("Legacy lerp speed (kept for compatibility). You can leave it unused when using SmoothDamp.")]
    public float pivotYFollowSpeed = 10f;

    [Tooltip("Seconds to reach the new pivot target. Smaller = snappier, bigger = smoother.")]
    public float pivotFollowSmoothTime = 0.12f;

    public enum PinchMode { Continuous = 0, Step = 1 }

    [Header("Pinch Zoom (Touch)")]
    [Tooltip("Continuous: pinch adjusts zoom continuously. Step: one pinch gesture = one scroll step.")]
    public PinchMode pinchMode = PinchMode.Continuous;

    public bool enablePinchZoom = true;

    [Tooltip("How much to move Z per 1 pixel change of pinch distance. Used only in Continuous mode.")]
    public float pinchZPerPixel = 0.005f;

    [Tooltip("Invert the pinch direction.")]
    public bool invertPinch = false;

    [Tooltip("If in Step mode, the minimum pixel change to register one step.")]
    public float pinchStepThreshold = 20f;    

    [Header("Ignore Areas")]
    public List<RectTransform> ignoreAreas = new List<RectTransform>();

    private float currentY = 0f;
    private float currentX = 0f;
    private float targetY = 0f;
    private float targetX = 0f;

    private int zoomIndex = 0;
    private float currentZ;
    private float targetZ;

    private Vector3 targetPivotPos;

    private bool isDragging = false;
    private Vector2 lastMousePos;

    private bool _isPinching = false;
    private float _prevPinchDist = 0f;

    // In Step mode, this prevents multiple steps from one continuous pinch gesture
    private bool _pinchStepConsumed = false;    

    // SmoothDamp velocity for pivot follow
    private Vector3 _pivotFollowVelocity;

    void OnEnable()
    {
        TrySubscribeOnTargetCharacterChanged();
        RefreshPivotFollowsFromTargetCharacter();
    }

    void OnDisable()
    {
        TryUnsubscribeOnTargetCharacterChanged();
    }

    void Start()
    {
        if (targetRotY != null)
        {
            currentY = targetY = initialRotY;
            targetRotY.localEulerAngles = new Vector3(0f, initialRotY, 0f);
        }

        if (targetRotX != null)
        {
            currentX = targetX = initialRotX;
            targetRotX.localEulerAngles = new Vector3(initialRotX, 0f, 0f);
        }

        if (targetZoom != null && zoomSteps.Count > 0)
        {
            currentZ = targetZ = zoomSteps[zoomIndex].zoomZ;
            Vector3 pos = targetZoom.localPosition;
            targetZoom.localPosition = new Vector3(pos.x, pos.y, targetZ);
        }

        if (pivotRoot != null && zoomSteps.Count > 0 && zoomSteps[zoomIndex].pivotFollow != null)
        {
            Vector3 basePos = zoomSteps[zoomIndex].pivotFollow.position;
            Vector3 offset = zoomSteps[zoomIndex].pivotOffset;
            targetPivotPos = basePos + offset;
            pivotRoot.position = targetPivotPos;
        }

        // ✅ NEW: Register multiple buttons for each zoom step
        for (int i = 0; i < zoomSteps.Count; i++)
        {
            int stepIndex = i; // 避免閉包問題
            foreach (var btn in zoomSteps[i].triggerButtons)
            {
                if (btn != null)
                {
                    btn.onClick.AddListener(() =>
                    {
                        SetZoomIndex(stepIndex);
                    });
                }
            }
        }
    }

    void Update()
    {
        if (Input.GetMouseButtonUp(0))
        {
            isDragging = false;
        }

        bool blockInput = IsMouseOverIgnoreArea() || IsAnyTouchOverUIOrIgnoreArea();

        if (!blockInput)
        {
            if (enablePinchZoom && Input.touchCount == 2)
            {
                HandlePinchZoom();
            }
            else
            {
                // If we were pinching and now we're not, reset pinch state.
                _isPinching = false;
                _pinchStepConsumed = false;
                HandleMouseInput();
            }
        }
        else
        {
            // UI / ignore area is being interacted with.
            _isPinching = false;
            _pinchStepConsumed = false;
        }

        ApplyRotation();
        ApplyZoom();
        ApplyPivotFollow();
    }

    void HandleMouseInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            isDragging = true;
            lastMousePos = Input.mousePosition;
        }

        if (isDragging)
        {
            Vector2 delta = (Vector2)Input.mousePosition - lastMousePos;
            lastMousePos = Input.mousePosition;

            targetY += delta.x * rotationSpeedY * Time.deltaTime;
            targetX -= delta.y * rotationSpeedX * Time.deltaTime;
            targetX = Mathf.Clamp(targetX, minX, maxX);
        }

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll > 0f)
        {
            SetZoomIndex(Mathf.Clamp(zoomIndex + 1, 0, zoomSteps.Count - 1));
        }
        else if (scroll < 0f)
        {
            SetZoomIndex(Mathf.Clamp(zoomIndex - 1, 0, zoomSteps.Count - 1));
        }
    }

    void HandlePinchZoom()
    {
        if (zoomSteps == null || zoomSteps.Count == 0) return;

        Touch t0 = Input.GetTouch(0);
        Touch t1 = Input.GetTouch(1);

        // If either finger is over UI or ignore areas, don't pinch.
        if (EventSystem.current != null)
        {
            if (EventSystem.current.IsPointerOverGameObject(t0.fingerId) ||
                EventSystem.current.IsPointerOverGameObject(t1.fingerId))
            {
                _isPinching = false;
                _pinchStepConsumed = false;
                return;
            }
        }

        if (IsScreenPosOverIgnoreArea(t0.position) || IsScreenPosOverIgnoreArea(t1.position))
        {
            _isPinching = false;
            _pinchStepConsumed = false;
            return;
        }

        float dist = Vector2.Distance(t0.position, t1.position);

        if (!_isPinching)
        {
            _isPinching = true;
            _prevPinchDist = dist;
            _pinchStepConsumed = false;
            return;
        }

        float deltaPixels = dist - _prevPinchDist;
        _prevPinchDist = dist;

        if (Mathf.Abs(deltaPixels) < 0.01f) return;

        if (invertPinch) deltaPixels = -deltaPixels;

        if (pinchMode == PinchMode.Continuous)
        {
            float dz = deltaPixels * pinchZPerPixel;

            GetZoomClampRange(out float zMin, out float zMax);
            targetZ = Mathf.Clamp(targetZ + dz, zMin, zMax);

            // Keep zoomIndex aligned for scroll-step / UI step logic.
            zoomIndex = FindClosestZoomIndex(targetZ);
        }
        else // Step mode: one step per pinch gesture once threshold is passed
        {
            if (_pinchStepConsumed) return;
            if (Mathf.Abs(deltaPixels) < pinchStepThreshold) return;

            if (deltaPixels > 0f)
                SetZoomIndex(Mathf.Clamp(zoomIndex + 1, 0, zoomSteps.Count - 1));
            else
                SetZoomIndex(Mathf.Clamp(zoomIndex - 1, 0, zoomSteps.Count - 1));

            _pinchStepConsumed = true;
        }
    }

    void GetZoomClampRange(out float zMin, out float zMax)
    {
        zMin = float.PositiveInfinity;
        zMax = float.NegativeInfinity;

        for (int i = 0; i < zoomSteps.Count; i++)
        {
            float z = zoomSteps[i].zoomZ;
            if (z < zMin) zMin = z;
            if (z > zMax) zMax = z;
        }

        if (float.IsInfinity(zMin) || float.IsInfinity(zMax))
        {
            zMin = zMax = 0f;
        }
    }

    int FindClosestZoomIndex(float z)
    {
        if (zoomSteps == null || zoomSteps.Count == 0) return 0;

        int best = 0;
        float bestDist = Mathf.Abs(zoomSteps[0].zoomZ - z);

        for (int i = 1; i < zoomSteps.Count; i++)
        {
            float d = Mathf.Abs(zoomSteps[i].zoomZ - z);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }

        return best;
    }

    void SetZoomIndex(int newIndex)
    {
        if (newIndex == zoomIndex) return;
        zoomIndex = newIndex;
        targetZ = zoomSteps[zoomIndex].zoomZ;

        // ✅ Smooth transition: reset velocity when switching follow target
        _pivotFollowVelocity = Vector3.zero;

        if (zoomSteps[zoomIndex].pivotFollow != null)
        {
            Vector3 basePos = zoomSteps[zoomIndex].pivotFollow.position;
            Vector3 offset = zoomSteps[zoomIndex].pivotOffset;
            targetPivotPos = basePos + offset;
        }
    }

    void ApplyRotation()
    {
        currentY = Mathf.Lerp(currentY, targetY, Time.deltaTime * brakeY);
        currentX = Mathf.Lerp(currentX, targetX, Time.deltaTime * brakeX);

        if (targetRotY != null)
            targetRotY.localEulerAngles = new Vector3(0f, currentY, 0f);
        if (targetRotX != null)
            targetRotX.localEulerAngles = new Vector3(currentX, 0f, 0f);
    }

    void ApplyZoom()
    {
        if (targetZoom != null)
        {
            currentZ = Mathf.Lerp(currentZ, targetZ, Time.deltaTime * zoomBrake);
            Vector3 pos = targetZoom.localPosition;
            targetZoom.localPosition = new Vector3(pos.x, pos.y, currentZ);
        }
    }

    void ApplyPivotFollow()
    {
        if (pivotRoot == null || zoomSteps == null || zoomSteps.Count <= zoomIndex) return;

        var follow = zoomSteps[zoomIndex].pivotFollow;
        if (follow == null) return;

        Vector3 basePos = follow.position;
        Vector3 offset = zoomSteps[zoomIndex].pivotOffset;
        targetPivotPos = basePos + offset;

        // ✅ SmoothDamp gives eased, natural smoothing when switching pivotFollow
        pivotRoot.position = Vector3.SmoothDamp(
            pivotRoot.position,
            targetPivotPos,
            ref _pivotFollowVelocity,
            Mathf.Max(0.0001f, pivotFollowSmoothTime)
        );
    }

    public void ForceUpdatePivotY()
    {
        if (zoomSteps.Count > zoomIndex && zoomSteps[zoomIndex].pivotFollow != null)
        {
            Vector3 basePos = zoomSteps[zoomIndex].pivotFollow.position;
            Vector3 offset = zoomSteps[zoomIndex].pivotOffset;
            targetPivotPos = basePos + offset;

            // Optional: clear velocity to avoid overshoot if force-updating in the middle
            _pivotFollowVelocity = Vector3.zero;
        }
    }

    bool IsScreenPosOverIgnoreArea(Vector2 screenPos)
    {
        if (EventSystem.current == null) return false;

        foreach (var area in ignoreAreas)
        {
            if (area != null && area.gameObject.activeInHierarchy &&
                RectTransformUtility.RectangleContainsScreenPoint(area, screenPos))
            {
                return true;
            }
        }
        return false;
    }

    bool IsMouseOverIgnoreArea()
    {
        return IsScreenPosOverIgnoreArea(Input.mousePosition);
    }

    bool IsAnyTouchOverUIOrIgnoreArea()
    {
        if (EventSystem.current == null) return false;

        // If any touch is over UI, block camera input.
        for (int i = 0; i < Input.touchCount; i++)
        {
            var t = Input.GetTouch(i);

            // Unity UI check
            if (EventSystem.current.IsPointerOverGameObject(t.fingerId))
                return true;

            // Custom ignore rects
            if (IsScreenPosOverIgnoreArea(t.position))
                return true;
        }

        return false;
    }

    // =========================================================
    // Auto link ZoomStep pivots from Target Character Source
    // =========================================================
    private object _subscribedEventOwner;
    private string _subscribedEventName;
    private Delegate _subscribedDelegate;
    private FieldInfo _subscribedDelegateField;

    private void TrySubscribeOnTargetCharacterChanged()
    {
        TryUnsubscribeOnTargetCharacterChanged();

        if (targetCharacterSource == null) return;

        var owner = targetCharacterSource;
        var t = owner.GetType();

        const string evtName = "onTargetCharacterChanged";

        // Try fields first
        var field = t.GetField(evtName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            var fieldValue = field.GetValue(owner);

            // UnityEvent / UnityEvent<T>
            if (TrySubscribeUnityEvent(fieldValue)) return;

            // Delegate field patterns: Action / Action<GameObject> / Action<Transform>
            if (TrySubscribeDelegateField(owner, field, fieldValue)) return;
        }

        // Try properties
        var prop = t.GetProperty(evtName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.GetIndexParameters().Length == 0)
        {
            if (TrySubscribeUnityEvent(prop.GetValue(owner, null))) return;
        }

        // C# event
        var ev = t.GetEvent(evtName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (ev != null)
        {
            var handlerType = ev.EventHandlerType;
            if (handlerType == null) return;

            MethodInfo method = null;

            if (handlerType == typeof(Action))
                method = GetType().GetMethod(nameof(HandleTargetCharacterChanged), BindingFlags.NonPublic | BindingFlags.Instance);
            else if (handlerType == typeof(Action<GameObject>))
                method = GetType().GetMethod(nameof(HandleTargetCharacterChangedGO), BindingFlags.NonPublic | BindingFlags.Instance);
            else if (handlerType == typeof(Action<Transform>))
                method = GetType().GetMethod(nameof(HandleTargetCharacterChangedTransform), BindingFlags.NonPublic | BindingFlags.Instance);

            if (method == null) return;

            try
            {
                var del = Delegate.CreateDelegate(handlerType, this, method);
                ev.AddEventHandler(owner, del);

                _subscribedEventOwner = owner;
                _subscribedEventName = evtName;
                _subscribedDelegate = del;
            }
            catch
            {
                // ignore
            }
        }
    }

    private bool TrySubscribeDelegateField(object owner, FieldInfo field, object fieldValue)
    {
        if (owner == null || field == null) return false;

        var delegateType = field.FieldType;
        if (!typeof(Delegate).IsAssignableFrom(delegateType)) return false;

        MethodInfo method = null;
        if (delegateType == typeof(Action))
            method = GetType().GetMethod(nameof(HandleTargetCharacterChanged), BindingFlags.NonPublic | BindingFlags.Instance);
        else if (delegateType == typeof(Action<GameObject>))
            method = GetType().GetMethod(nameof(HandleTargetCharacterChangedGO), BindingFlags.NonPublic | BindingFlags.Instance);
        else if (delegateType == typeof(Action<Transform>))
            method = GetType().GetMethod(nameof(HandleTargetCharacterChangedTransform), BindingFlags.NonPublic | BindingFlags.Instance);
        else
            return false;

        if (method == null) return false;

        try
        {
            var myHandler = Delegate.CreateDelegate(delegateType, this, method);

            var current = fieldValue as Delegate;
            var combined = Delegate.Combine(current, myHandler);
            field.SetValue(owner, combined);

            _subscribedEventOwner = owner;
            _subscribedEventName = field.Name;
            _subscribedDelegate = myHandler;
            _subscribedDelegateField = field;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySubscribeUnityEvent(object maybeUnityEvent)
    {
        if (maybeUnityEvent == null) return false;

        if (maybeUnityEvent is UnityEvent uev)
        {
            uev.AddListener(HandleTargetCharacterChanged);
            _subscribedEventOwner = targetCharacterSource;
            _subscribedEventName = "onTargetCharacterChanged";
            _subscribedDelegate = null;
            _subscribedDelegateField = null;
            return true;
        }

        if (maybeUnityEvent is UnityEvent<GameObject> uevGO)
        {
            uevGO.AddListener(HandleTargetCharacterChangedGO);
            _subscribedEventOwner = targetCharacterSource;
            _subscribedEventName = "onTargetCharacterChanged";
            _subscribedDelegate = null;
            _subscribedDelegateField = null;
            return true;
        }

        if (maybeUnityEvent is UnityEvent<Transform> uevTr)
        {
            uevTr.AddListener(HandleTargetCharacterChangedTransform);
            _subscribedEventOwner = targetCharacterSource;
            _subscribedEventName = "onTargetCharacterChanged";
            _subscribedDelegate = null;
            _subscribedDelegateField = null;
            return true;
        }

        return false;
    }

    private void TryUnsubscribeOnTargetCharacterChanged()
    {
        if (_subscribedEventOwner == null || targetCharacterSource == null) return;

        // Delegate FIELD (Action / Action<T>)
        if (_subscribedDelegateField != null && _subscribedDelegate != null)
        {
            try
            {
                var current = _subscribedDelegateField.GetValue(_subscribedEventOwner) as Delegate;
                if (current != null)
                {
                    var removed = Delegate.Remove(current, _subscribedDelegate);
                    _subscribedDelegateField.SetValue(_subscribedEventOwner, removed);
                }
            }
            catch { /* ignore */ }

            _subscribedDelegateField = null;
            _subscribedDelegate = null;
            _subscribedEventOwner = null;
            _subscribedEventName = null;
            return;
        }

        var owner = targetCharacterSource;
        var t = owner.GetType();
        const string evtName = "onTargetCharacterChanged";

        // UnityEvent field/property
        var field = t.GetField(evtName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            var v = field.GetValue(owner);
            if (v is UnityEvent uev) uev.RemoveListener(HandleTargetCharacterChanged);
            else if (v is UnityEvent<GameObject> uevGO) uevGO.RemoveListener(HandleTargetCharacterChangedGO);
            else if (v is UnityEvent<Transform> uevTr) uevTr.RemoveListener(HandleTargetCharacterChangedTransform);
        }

        var prop = t.GetProperty(evtName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.GetIndexParameters().Length == 0)
        {
            var v = prop.GetValue(owner, null);
            if (v is UnityEvent uev) uev.RemoveListener(HandleTargetCharacterChanged);
            else if (v is UnityEvent<GameObject> uevGO) uevGO.RemoveListener(HandleTargetCharacterChangedGO);
            else if (v is UnityEvent<Transform> uevTr) uevTr.RemoveListener(HandleTargetCharacterChangedTransform);
        }

        // C# event
        if (_subscribedDelegate != null)
        {
            var ev = t.GetEvent(evtName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ev != null)
            {
                try { ev.RemoveEventHandler(owner, _subscribedDelegate); } catch { }
            }
        }

        _subscribedEventOwner = null;
        _subscribedEventName = null;
        _subscribedDelegate = null;
        _subscribedDelegateField = null;
    }

    // Handles (no args)
    private void HandleTargetCharacterChanged()
    {
        RefreshPivotFollowsFromTargetCharacter();
    }

    // Handles (GameObject arg)
    private void HandleTargetCharacterChangedGO(GameObject _)
    {
        RefreshPivotFollowsFromTargetCharacter();
    }

    // Handles (Transform arg)
    private void HandleTargetCharacterChangedTransform(Transform _)
    {
        RefreshPivotFollowsFromTargetCharacter();
    }

    [ContextMenu("Refresh Pivot Follows From Target Character Source")]
    public void RefreshPivotFollowsFromTargetCharacter()
    {
        var target = ResolveTargetCharacterFromSource();
        if (target == null) return;

        // Find Avatar_Spine and Avatar_Head under target
        var spine = FindDeepChild(target.transform, "Avatar_Spine");
        var head = FindDeepChild(target.transform, "Avatar_Head");

        if (zoomSteps != null && zoomSteps.Count > 0 && spine != null)
        {
            zoomSteps[0].pivotFollow = spine;
        }

        if (zoomSteps != null && zoomSteps.Count > 1 && head != null)
        {
            zoomSteps[1].pivotFollow = head;
        }
    }

    private GameObject ResolveTargetCharacterFromSource()
    {
        if (targetCharacterSource == null) return null;

        var owner = targetCharacterSource;
        var t = owner.GetType();

        // Try "targetCharacter" field/property (common camelCase)
        var go = ReadGOOrTransformFromMember(t, owner, "targetCharacter");
        if (go != null) return go;

        // Try "TargetCharacter" field/property (PascalCase)
        go = ReadGOOrTransformFromMember(t, owner, "TargetCharacter");
        if (go != null) return go;

        // Try "target_character"
        go = ReadGOOrTransformFromMember(t, owner, "target_character");
        if (go != null) return go;

        return null;
    }

    private GameObject ReadGOOrTransformFromMember(System.Type t, object owner, string memberName)
    {
        var f = t.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null)
        {
            var v = f.GetValue(owner);
            if (v is GameObject go) return go;
            if (v is Transform tr) return tr.gameObject;
        }

        var p = t.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p != null && p.GetIndexParameters().Length == 0)
        {
            var v = p.GetValue(owner, null);
            if (v is GameObject go) return go;
            if (v is Transform tr) return tr.gameObject;
        }

        return null;
    }

    private Transform FindDeepChild(Transform parent, string name)
    {
        if (parent == null) return null;
        var stack = new Stack<Transform>();
        stack.Push(parent);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.name == name) return cur;

            for (int i = 0; i < cur.childCount; i++)
                stack.Push(cur.GetChild(i));
        }
        return null;
    }
}
