using System;
using UnityEngine;

/// <summary>
/// Track 1: 이미 일어난 과거형 사실을 구독하는 채널이다.
/// Payload는 가능한 한 변경 불가능한 <c>readonly struct</c>로 정의한다.
/// </summary>
public interface IEventSubscriber<T>
    where T : struct
{
    /// <summary>
    /// 콜백을 구독한다. 같은 delegate를 다시 구독하면 중복 등록하지 않고 호출 순서의 끝으로 옮긴다.
    /// static manager가 파괴된 객체를 계속 참조하지 않도록 같은 lifecycle에서 반드시 해제한다.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/>이 null이면 발생한다.</exception>
    void Subscribe(Action<T> callback);

    /// <summary>
    /// 이전에 등록한 콜백을 해제한다.
    /// </summary>
    void Unsubscribe(Action<T> callback);
}

/// <summary>
/// Track 1: 이미 일어난 과거형 사실을 동기 발행한다.
/// </summary>
public interface IEventPublisher<T>
    where T : struct
{
    /// <summary>
    /// 필드가 없는 marker/signal용 이벤트를 기본값으로 발행한다.
    /// </summary>
    void Publish();

    /// <summary>
    /// 이벤트를 호출한 스레드에서 즉시 전달한다.
    /// </summary>
    void Publish(T eventData);
}

/// <summary>
/// Track 2: 현재 값이 즉시 필요한 읽기 전용 Query provider를 등록한다.
/// </summary>
public interface IProvider<TRequest, TResponse>
    where TRequest : struct
{
    /// <summary>
    /// 현재 provider를 등록하거나 교체한다.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/>가 null이면 발생한다.</exception>
    void Register(Func<TRequest, TResponse> handler);

    /// <summary>
    /// 현재 provider를 해제한다.
    /// </summary>
    void Unregister();
}

/// <summary>
/// Track 2: 등록된 provider에 읽기 전용 Query를 동기 전달한다.
/// </summary>
public interface IRequester<TRequest, TResponse>
    where TRequest : struct
{
    /// <summary>
    /// provider가 없으면 <typeparamref name="TResponse"/>의 기본값을 반환한다.
    /// </summary>
    TResponse Send(TRequest request);
}

/// <summary>
/// Track 1 Event와 Track 2 Query 채널의 전역 진입점이다.
/// Runtime state를 소유하지 않으며 각 등록은 수동 해제 또는 ClearAll로 정리한다.
/// </summary>
public static class EventManager
{
    private static Action _onClearAll;

    /// <summary>
    /// 지정한 이벤트 타입의 발행 전용 채널을 반환한다.
    /// </summary>
    public static IEventPublisher<T> GetPublisher<T>()
        where T : struct
    {
        return EventStorage<T>.Channel;
    }

    /// <summary>
    /// 지정한 이벤트 타입의 구독 전용 채널을 반환한다.
    /// </summary>
    public static IEventSubscriber<T> GetSubscriber<T>()
        where T : struct
    {
        return EventStorage<T>.Channel;
    }

    /// <summary>
    /// 지정한 요청과 응답 타입의 Query provider 등록 채널을 반환한다.
    /// </summary>
    public static IProvider<TRequest, TResponse> GetProvider<TRequest, TResponse>()
        where TRequest : struct
    {
        return QueryStorage<TRequest, TResponse>.Channel;
    }

    /// <summary>
    /// 지정한 요청과 응답 타입의 Query 요청 채널을 반환한다.
    /// </summary>
    public static IRequester<TRequest, TResponse> GetRequester<TRequest, TResponse>()
        where TRequest : struct
    {
        return QueryStorage<TRequest, TResponse>.Channel;
    }

    /// <summary>
    /// 생성된 모든 채널의 구독과 provider 등록을 지운다.
    /// 채널 registry는 유지해 같은 generic type을 이후에도 다시 정리할 수 있게 한다.
    /// </summary>
    public static void ClearAll()
    {
        if (_onClearAll != null)
        {
            _onClearAll.Invoke();
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ClearBeforeRuntimeInitialization()
    {
        // Domain Reload를 꺼도 이전 Play session의 callback과 handler가 남지 않게 한다.
        ClearAll();
    }

    private static class EventStorage<T>
        where T : struct
    {
        public static readonly EventChannel<T> Channel = new EventChannel<T>();

        static EventStorage()
        {
            _onClearAll += Channel.Clear;
        }
    }

    private static class QueryStorage<TRequest, TResponse>
        where TRequest : struct
    {
        public static readonly QueryChannel<TRequest, TResponse> Channel =
            new QueryChannel<TRequest, TResponse>();

        static QueryStorage()
        {
            _onClearAll += Channel.Clear;
        }
    }

    private sealed class EventChannel<T> : IEventSubscriber<T>, IEventPublisher<T>
        where T : struct
    {
        private Action<T> _callback;

        public void Subscribe(Action<T> callback)
        {
            if (callback == null)
            {
#if UNITY_EDITOR
                Debug.LogError("EventManager.Subscribe: callback 매개변수가 null입니다.");
#endif
                throw new ArgumentNullException(nameof(callback));
            }

            // OnEnable 재진입 같은 동일 callback 재구독은 하나만 유지하고 호출 순서 끝으로 옮긴다.
            _callback -= callback;
            _callback += callback;
        }

        public void Unsubscribe(Action<T> callback)
        {
            _callback -= callback;
        }

        public void Publish()
        {
            Publish(default(T));
        }

        public void Publish(T eventData)
        {
#if UNITY_EDITOR
            Action<T> callback = _callback;
            if (callback == null)
            {
                return;
            }

            Delegate[] subscribers = callback.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                try
                {
                    ((Action<T>)subscribers[i]).Invoke(eventData);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
#else
            if (_callback != null)
            {
                _callback.Invoke(eventData);
            }
#endif
        }

        public void Clear()
        {
            _callback = null;
        }
    }

    private sealed class QueryChannel<TRequest, TResponse> :
        IProvider<TRequest, TResponse>,
        IRequester<TRequest, TResponse>
        where TRequest : struct
    {
        private Func<TRequest, TResponse> _handler;

        public void Register(Func<TRequest, TResponse> handler)
        {
            if (handler == null)
            {
#if UNITY_EDITOR
                Debug.LogError("EventManager.Register: handler 매개변수가 null입니다.");
#endif
                throw new ArgumentNullException(nameof(handler));
            }

            if (_handler != null)
            {
#if UNITY_EDITOR
                // Editor에서는 의도하지 않은 중복 등록을 알리되 최신 provider로 교체한다.
                Debug.LogWarning(
                    $"[EventManager] Query provider가 이미 등록되어 최신 handler로 교체합니다. " +
                    $"Request={typeof(TRequest).FullName}, Response={typeof(TResponse).FullName}");
#endif
            }

            _handler = handler;
        }

        public void Unregister()
        {
            _handler = null;
        }

        public TResponse Send(TRequest request)
        {
            if (_handler == null)
            {
                return default(TResponse);
            }

            return _handler.Invoke(request);
        }

        public void Clear()
        {
            _handler = null;
        }
    }
}
