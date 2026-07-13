using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

/// <summary>
/// 현재 게임 세션에서 이미 일어난 사실을 동기 전달한다.
/// </summary>
public interface IEventBus : IDisposable
{
    /// <summary>
    /// 필드가 없는 marker 이벤트를 발행한다.
    /// </summary>
    void Publish<T>() where T : struct;

    /// <summary>
    /// 이벤트를 Unity 메인 스레드에서 즉시 전달한다.
    /// </summary>
    void Publish<T>(T eventData) where T : struct;

    /// <summary>
    /// 이벤트를 구독한다. 반환된 token은 구독한 lifecycle에서 Dispose해야 한다.
    /// </summary>
    IDisposable Subscribe<T>(Action<T> callback) where T : struct;
}

/// <summary>
/// GameSession 또는 GameplayRoot가 생성하고 세션 종료 시 Dispose하는 EventBus다.
/// </summary>
public sealed class EventManager : IEventBus
{
    private readonly Dictionary<Type, IEventChannel> _channels =
        new Dictionary<Type, IEventChannel>();
    private readonly int _mainThreadId;
    private bool _isDisposed;

    public EventManager()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    public void Publish<T>() where T : struct
    {
        Publish(default(T));
    }

    public void Publish<T>(T eventData) where T : struct
    {
        ThrowIfDisposed();
        ThrowIfNotMainThread();

        IEventChannel channel;
        if (!_channels.TryGetValue(typeof(T), out channel))
        {
            return;
        }

        ((EventChannel<T>)channel).Publish(eventData);
    }

    public IDisposable Subscribe<T>(Action<T> callback) where T : struct
    {
        ThrowIfDisposed();

        if (callback == null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        IEventChannel channel;
        if (!_channels.TryGetValue(typeof(T), out channel))
        {
            channel = new EventChannel<T>();
            _channels.Add(typeof(T), channel);
        }

        return ((EventChannel<T>)channel).Subscribe(callback);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        foreach (IEventChannel channel in _channels.Values)
        {
            channel.Clear();
        }

        _channels.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            ThrowContractViolation(new ObjectDisposedException(nameof(EventManager)));
        }
    }

    private void ThrowIfNotMainThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
        {
            ThrowContractViolation(
                new InvalidOperationException(
                    "EventManager.Publish는 EventManager를 생성한 Unity 메인 스레드에서 호출해야 합니다."));
        }
    }

    private static void ThrowContractViolation(Exception exception)
    {
#if UNITY_EDITOR
        Debug.LogException(exception);
#endif
        throw exception;
    }

    private interface IEventChannel
    {
        void Clear();
    }

    private sealed class EventChannel<T> : IEventChannel where T : struct
    {
        private readonly List<Subscription> _subscriptions = new List<Subscription>();

        public IDisposable Subscribe(Action<T> callback)
        {
            Subscription subscription = new Subscription(this, callback);
            _subscriptions.Add(subscription);
            return subscription;
        }

        public void Publish(T eventData)
        {
            Subscription[] snapshot = _subscriptions.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Action<T> callback = snapshot[i].Callback;

                try
                {
                    callback.Invoke(eventData);
                }
                catch (Exception exception)
                {
                    LogSubscriberException(callback, exception);
                }
            }
        }

        public void Clear()
        {
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                _subscriptions[i].Detach();
            }

            _subscriptions.Clear();
        }

        private void Remove(Subscription subscription)
        {
            _subscriptions.Remove(subscription);
        }

        private static void LogSubscriberException(Action<T> callback, Exception exception)
        {
            string targetType = callback.Target == null
                ? "<static>"
                : callback.Target.GetType().FullName;
            string declaringType = callback.Method.DeclaringType == null
                ? "<unknown>"
                : callback.Method.DeclaringType.FullName;
            string message =
                $"[EventManager] Subscriber exception. Event={typeof(T).FullName}, " +
                $"Target={targetType}, Method={declaringType}.{callback.Method.Name}";

            Debug.LogException(
                new InvalidOperationException(
                    $"{message}\n" +
                    $"Original={exception.GetType().FullName}: {exception.Message}"));
        }

        private sealed class Subscription : IDisposable
        {
            private EventChannel<T> _owner;

            public Subscription(EventChannel<T> owner, Action<T> callback)
            {
                _owner = owner;
                Callback = callback;
            }

            public Action<T> Callback { get; }

            public void Dispose()
            {
                EventChannel<T> owner = _owner;
                if (owner == null)
                {
                    return;
                }

                _owner = null;
                owner.Remove(this);
            }

            public void Detach()
            {
                _owner = null;
            }
        }
    }
}
