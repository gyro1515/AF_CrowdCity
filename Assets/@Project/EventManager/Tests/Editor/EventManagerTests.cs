using System;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class EventManagerTests
{
    private EventManager _events;

    [SetUp]
    public void SetUp()
    {
        _events = new EventManager();
    }

    [TearDown]
    public void TearDown()
    {
        _events.Dispose();
    }

    [Test]
    public void Publish_DeliversEventSynchronously()
    {
        int receivedValue = 0;
        _events.Subscribe<TestEvent>(eventData => receivedValue = eventData.Value);

        _events.Publish(new TestEvent(7));

        Assert.That(receivedValue, Is.EqualTo(7));
    }

    [Test]
    public void SubscriptionDispose_StopsFutureDelivery()
    {
        int callCount = 0;
        IDisposable subscription = _events.Subscribe<TestEvent>(_ => callCount++);

        subscription.Dispose();
        subscription.Dispose();
        _events.Publish(new TestEvent(1));

        Assert.That(callCount, Is.Zero);
    }

    [Test]
    public void DispatchMutation_AffectsNextPublishOnly()
    {
        string calls = string.Empty;
        IDisposable second = null;
        IDisposable addedDuringDispatch = null;

        _events.Subscribe<TestEvent>(_ =>
        {
            calls += "A";
            second.Dispose();
            if (addedDuringDispatch == null)
            {
                addedDuringDispatch = _events.Subscribe<TestEvent>(__ => calls += "C");
            }
        });
        second = _events.Subscribe<TestEvent>(_ => calls += "B");

        _events.Publish(new TestEvent(1));
        Assert.That(calls, Is.EqualTo("AB"));

        calls = string.Empty;
        _events.Publish(new TestEvent(2));
        Assert.That(calls, Is.EqualTo("AC"));
    }

    [Test]
    public void SubscriberException_DoesNotStopRemainingSubscribers()
    {
        int callCount = 0;
        _events.Subscribe<TestEvent>(_ => throw new Exception("expected subscriber failure"));
        _events.Subscribe<TestEvent>(_ => callCount++);
        LogAssert.Expect(
            LogType.Exception,
            new Regex("Subscriber exception.*TestEvent", RegexOptions.Singleline));

        _events.Publish(new TestEvent(1));

        Assert.That(callCount, Is.EqualTo(1));
    }

    [Test]
    public void EventTypes_HaveSeparateSubscriptions()
    {
        int testEventCalls = 0;
        int otherEventCalls = 0;
        _events.Subscribe<TestEvent>(_ => testEventCalls++);
        _events.Subscribe<OtherEvent>(_ => otherEventCalls++);

        _events.Publish(new TestEvent(1));

        Assert.That(testEventCalls, Is.EqualTo(1));
        Assert.That(otherEventCalls, Is.Zero);
    }

    [Test]
    public void Dispose_ClearsSubscriptionsAndRejectsFurtherUse()
    {
        IDisposable subscription = _events.Subscribe<TestEvent>(_ => { });

        _events.Dispose();

        Assert.DoesNotThrow(subscription.Dispose);
        LogAssert.Expect(
            LogType.Exception,
            new Regex("ObjectDisposedException.*EventManager", RegexOptions.Singleline));
        Assert.Throws<ObjectDisposedException>(() => _events.Publish(new TestEvent(1)));
        LogAssert.Expect(
            LogType.Exception,
            new Regex("ObjectDisposedException.*EventManager", RegexOptions.Singleline));
        Assert.Throws<ObjectDisposedException>(() => _events.Subscribe<TestEvent>(_ => { }));
    }

    [Test]
    public void DisposedBus_LogsAndThrowsSameExceptionInstance()
    {
        _events.Dispose();
        ILogHandler originalHandler = Debug.unityLogger.logHandler;
        CapturingLogHandler capturingHandler = new CapturingLogHandler(originalHandler);
        Debug.unityLogger.logHandler = capturingHandler;

        try
        {
            LogAssert.Expect(
                LogType.Exception,
                new Regex("ObjectDisposedException.*EventManager", RegexOptions.Singleline));

            ObjectDisposedException thrown = Assert.Throws<ObjectDisposedException>(
                () => _events.Publish(new TestEvent(1)));

            Assert.That(capturingHandler.LastException, Is.SameAs(thrown));
        }
        finally
        {
            Debug.unityLogger.logHandler = originalHandler;
        }
    }

    [Test]
    public void PublishFromWorkerThread_LogsAndThrowsMainThreadViolation()
    {
        Exception workerException = null;
        LogAssert.Expect(
            LogType.Exception,
            new Regex("InvalidOperationException.*Unity main thread", RegexOptions.Singleline));
        Thread worker = new Thread(() =>
        {
            try
            {
                _events.Publish(new TestEvent(1));
            }
            catch (Exception exception)
            {
                workerException = exception;
            }
        });

        worker.Start();
        bool joined = worker.Join(TimeSpan.FromSeconds(5));

        Assert.That(joined, Is.True, "Worker thread가 제한 시간 안에 종료되어야 합니다.");
        Assert.That(workerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(workerException.Message, Does.Contain("Unity main thread"));
    }

    [Test]
    public void SubscriberInvalidOperationException_IsLoggedOnceAndIsolated()
    {
        int callCount = 0;
        _events.Subscribe<TestEvent>(_ => throw new InvalidOperationException("expected invalid operation"));
        _events.Subscribe<TestEvent>(_ => callCount++);
        LogAssert.Expect(
            LogType.Exception,
            new Regex("Subscriber exception.*TestEvent", RegexOptions.Singleline));

        _events.Publish(new TestEvent(1));

        Assert.That(callCount, Is.EqualTo(1));
        LogAssert.NoUnexpectedReceived();
    }

    [Test]
    public void ParameterlessPublish_DeliversDefaultMarker()
    {
        int callCount = 0;
        _events.Subscribe<MarkerEvent>(_ => callCount++);

        _events.Publish<MarkerEvent>();

        Assert.That(callCount, Is.EqualTo(1));
    }

    private readonly struct TestEvent
    {
        public TestEvent(int value)
        {
            Value = value;
        }

        public int Value { get; }
    }

    private readonly struct OtherEvent
    {
    }

    private readonly struct MarkerEvent
    {
    }

    private sealed class CapturingLogHandler : ILogHandler
    {
        private readonly ILogHandler _inner;

        public CapturingLogHandler(ILogHandler inner)
        {
            _inner = inner;
        }

        public Exception LastException { get; private set; }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            LastException = exception;
            _inner.LogException(exception, context);
        }

        public void LogFormat(
            LogType logType,
            UnityEngine.Object context,
            string format,
            params object[] args)
        {
            _inner.LogFormat(logType, context, format, args);
        }
    }
}
