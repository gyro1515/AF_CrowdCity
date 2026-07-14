using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// New Input System 폴링으로 드래그 heading과 재시작 입력을 감지하는 입력 feature root다.
/// 자체 Update 없이 GameplayRoot가 호출하는 <see cref="Poll"/> 안에서만 장치를 읽는다 (single-driver 규칙).
/// 화면 드래그 (x, y)는 world (x, z) 방향으로 그대로 매핑한다 (카메라 yaw 0 고정).
/// </summary>
public sealed class InputRoot : MonoBehaviour
{
    private const float DeadzonePixels = 20f;
    private const float DeadzoneSqr = DeadzonePixels * DeadzonePixels;

    private Vector2 _headingDir;
    private bool _hasHeading;
    private bool _firstDragRaised;
    private bool _wasPressed;
    private Vector2 _pressOrigin;
    private bool _dragExceededDeadzone;
    private bool _isShutdown;
#if UNITY_EDITOR
    private bool _syntheticActive;
#endif

    /// <summary>
    /// 드래그로 결정된 world XZ 방향이다. 정규화된 값이며 release 후에도 유지되고 첫 드래그 전에는 (0, 0)이다.
    /// </summary>
    public Vector2 HeadingDir
    {
        get { return _headingDir; }
    }

    /// <summary>
    /// 드래그가 deadzone을 넘어 heading이 한 번이라도 결정되었는지 여부다. release 후에도 유지된다.
    /// </summary>
    public bool HasHeading
    {
        get { return _hasHeading; }
    }

    /// <summary>
    /// 드래그가 처음으로 20 px deadzone을 넘는 순간 단 한 번만 발생한다.
    /// </summary>
    public event Action FirstDrag;

    /// <summary>
    /// R 키, 또는 deadzone을 넘지 않은 press+release(탭)에서 발생한다. 상태 gating은 여기서 하지 않는다.
    /// </summary>
    public event Action RestartTapped;

    /// <summary>
    /// 폴링 상태를 초기화한다. deadzone은 고정 상수라 config에서 읽는 값은 없다.
    /// </summary>
    public void Initialize(GameConfigSO config)
    {
        _headingDir = Vector2.zero;
        _hasHeading = false;
        _firstDragRaised = false;
        _wasPressed = false;
        _pressOrigin = Vector2.zero;
        _dragExceededDeadzone = false;
        _isShutdown = false;
#if UNITY_EDITOR
        _syntheticActive = false;
#endif
    }

    /// <summary>
    /// 입력 장치를 지금 읽는다. GameplayRoot Update의 첫 단계에서 프레임당 한 번 호출된다.
    /// Mouse/Touchscreen/Keyboard가 없어도 null 체크로 안전하게 동작하며 절대 throw하지 않는다.
    /// </summary>
    public void Poll()
    {
        if (_isShutdown)
        {
            return;
        }

#if UNITY_EDITOR
        if (_syntheticActive)
        {
            // 합성 heading이 켜져 있는 동안에는 장치 폴링을 통째로 무시한다.
            return;
        }
#endif

        bool restartRequested = false;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
        {
            restartRequested = true;
        }

        bool pressed = false;
        Vector2 screenPos = Vector2.zero;

        Touchscreen touchscreen = Touchscreen.current;
        if (touchscreen != null && touchscreen.primaryTouch.press.isPressed)
        {
            pressed = true;
            screenPos = touchscreen.primaryTouch.position.ReadValue();
        }

        if (!pressed)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed)
            {
                pressed = true;
                screenPos = mouse.position.ReadValue();
            }
        }

        if (pressed)
        {
            if (!_wasPressed)
            {
                _pressOrigin = screenPos;
                _dragExceededDeadzone = false;
            }

            Vector2 drag = screenPos - _pressOrigin;
            if (drag.sqrMagnitude > DeadzoneSqr)
            {
                // 화면 (x, y) 드래그를 world (x, z) 방향으로 매핑한다.
                _headingDir = drag.normalized;
                _hasHeading = true;
                _dragExceededDeadzone = true;
                RaiseFirstDragOnce();
            }
        }
        else if (_wasPressed && !_dragExceededDeadzone)
        {
            // deadzone을 넘지 않은 press+release는 탭으로 취급한다.
            restartRequested = true;
        }

        _wasPressed = pressed;

        if (restartRequested)
        {
            Action handler = RestartTapped;
            if (handler != null)
            {
                handler.Invoke();
            }
        }
    }

    /// <summary>
    /// 이벤트 delegate와 폴링 상태를 모두 지운다. 여러 번 호출해도 안전하다.
    /// </summary>
    public void Shutdown()
    {
        FirstDrag = null;
        RestartTapped = null;
        _headingDir = Vector2.zero;
        _hasHeading = false;
        _firstDragRaised = false;
        _wasPressed = false;
        _pressOrigin = Vector2.zero;
        _dragExceededDeadzone = false;
#if UNITY_EDITOR
        _syntheticActive = false;
#endif
        _isShutdown = true;
    }

#if UNITY_EDITOR
    /// <summary>
    /// play-smoke용 합성 heading을 설정한다. <see cref="ClearSynthetic"/> 전까지 장치 폴링을 대체하며
    /// 실제 드래그처럼 <see cref="FirstDrag"/>를 한 번만 발생시킨다.
    /// </summary>
    public void SetSyntheticHeading(Vector2 dir)
    {
        if (dir.sqrMagnitude <= 0f)
        {
            return;
        }

        _syntheticActive = true;
        _headingDir = dir.normalized;
        _hasHeading = true;
        _wasPressed = false;
        _dragExceededDeadzone = false;
        RaiseFirstDragOnce();
    }

    /// <summary>
    /// 합성 heading을 해제하고 장치 폴링으로 복귀한다. heading 자체는 유지된다.
    /// </summary>
    public void ClearSynthetic()
    {
        _syntheticActive = false;
    }

    /// <summary>
    /// play-smoke용으로 <see cref="RestartTapped"/>를 즉시 발생시킨다.
    /// </summary>
    public void DebugTapRestart()
    {
        Action handler = RestartTapped;
        if (handler != null)
        {
            handler.Invoke();
        }
    }
#endif

    private void RaiseFirstDragOnce()
    {
        if (_firstDragRaised)
        {
            return;
        }

        _firstDragRaised = true;
        Action handler = FirstDrag;
        if (handler != null)
        {
            handler.Invoke();
        }
    }
}
