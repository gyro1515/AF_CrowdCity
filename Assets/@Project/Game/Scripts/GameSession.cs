using System;
using System.Collections.Generic;

/// <summary>
/// 매치 상태 머신, 카운트다운 타이머, 팀별 인원/제거 현황을 소유하는 plain C# 세션이다.
/// CrowdRoot가 발행하는 <see cref="CrowdCountChangedEvent"/>와 <see cref="CrowdEliminatedEvent"/>를 구독해
/// 팀별 인원 배열과 제거 배열(크기 RivalCount+1)을 유지하고, 종료 판정은 <see cref="MatchRules"/>에 위임한다.
/// Ready -> Playing -> Finished로만 전이하며 전이당 <see cref="StateChanged"/>를 한 번만 발생시킨다.
/// 같은 batch에서 player와 rival이 함께 제거되면 패배가 우선한다
/// (CrowdRoot의 발행 순서 계약이 player 제거 이벤트를 먼저 보내고, Finish가 idempotent라 뒤따르는 제거는 무시된다).
/// </summary>
public sealed class GameSession : IGameSessionReadOnly, IDisposable
{
    private readonly int[] _countsByTeam;
    private readonly bool[] _eliminated;

    // EventManager.Subscribe는 token을 반환하지 않으므로, 구독 해제용으로 보관하는 delegate가 곧 이 세션의 token이다.
    private readonly Action<CrowdCountChangedEvent> _onCrowdCountChanged;
    private readonly Action<CrowdEliminatedEvent> _onCrowdEliminated;

    private bool _disposed;

    /// <summary>
    /// 설정 SO에서 매치 시간과 팀 수를 읽어 세션을 Ready 상태로 생성한다. 아직 bus 구독은 하지 않는다.
    /// </summary>
    public GameSession(GameConfigSO config)
    {
        int teamCount = config.RivalCount + 1;
        _countsByTeam = new int[teamCount];
        _eliminated = new bool[teamCount];

        // 모두 lone leader로 시작한다. SpawnInitial의 첫 coalesced 발행이 실제 값으로 덮어쓴다.
        for (int i = 0; i < teamCount; i++)
        {
            _countsByTeam[i] = 1;
        }

        TimeRemaining = config.MatchSeconds;
        WinnerTeam = -1;
        PlayerWon = false;
        State = MatchState.Ready;

        _onCrowdCountChanged = OnCrowdCountChanged;
        _onCrowdEliminated = OnCrowdEliminated;
    }

    /// <summary>
    /// 두 crowd bus 이벤트를 <see cref="EventManager.GetSubscriber{T}"/>로 구독한다.
    /// 같은 delegate 재구독은 EventManager가 중복 등록하지 않으므로 중복 호출에도 안전하다.
    /// 구독은 <see cref="Dispose"/>에서 해제한다.
    /// </summary>
    public void Initialize()
    {
        if (_disposed)
        {
            return;
        }

        EventManager.GetSubscriber<CrowdCountChangedEvent>().Subscribe(_onCrowdCountChanged);
        EventManager.GetSubscriber<CrowdEliminatedEvent>().Subscribe(_onCrowdEliminated);
    }

    /// <summary>
    /// 현재 매치 상태를 반환한다.
    /// </summary>
    public MatchState State { get; private set; }

    /// <summary>
    /// 남은 매치 시간을 초 단위로 반환한다.
    /// </summary>
    public float TimeRemaining { get; private set; }

    /// <summary>
    /// 승리한 팀 id를 반환한다. <see cref="MatchState.Finished"/> 이전에는 -1이다.
    /// </summary>
    public int WinnerTeam { get; private set; }

    /// <summary>
    /// 플레이어(팀 0)가 승리했는지 반환한다. <see cref="MatchState.Finished"/> 이전에는 false다.
    /// </summary>
    public bool PlayerWon { get; private set; }

    /// <summary>
    /// 매치 상태가 전이될 때마다 새 상태와 함께 발생한다. 전이당 한 번만 발생한다.
    /// 구독한 lifecycle과 같은 lifecycle에서 반드시 해제한다.
    /// </summary>
    public event Action<MatchState> StateChanged;

    /// <summary>
    /// Ready에서 Playing으로 전이한다. Ready가 아니면 아무 일도 하지 않는다.
    /// </summary>
    public void Begin()
    {
        if (State != MatchState.Ready)
        {
            return;
        }

        State = MatchState.Playing;
        StateChanged?.Invoke(MatchState.Playing);
    }

    /// <summary>
    /// Playing 중에만 남은 시간을 dt만큼 줄인다.
    /// 0 이하가 되면 0으로 고정하고 <see cref="MatchRules.ResolveWinner"/>로 승자를 확정해 종료한다.
    /// </summary>
    public void Tick(float dt)
    {
        if (State != MatchState.Playing)
        {
            return;
        }

        TimeRemaining -= dt;
        if (TimeRemaining > 0f)
        {
            return;
        }

        TimeRemaining = 0f;
        Finish(MatchRules.ResolveWinner(_countsByTeam, _eliminated));
    }

    /// <summary>
    /// 현재 팀별 인원/제거 현황으로 <see cref="MatchRules.ResolveStandings"/>를 호출해 results를 채운다.
    /// results는 먼저 비운 뒤 채워지며, Finished 이후에는 내부 배열이 더 갱신되지 않아 결과가 고정된다.
    /// </summary>
    public void GetStandings(List<CrowdStanding> results)
    {
        if (results == null)
        {
            return;
        }

        MatchRules.ResolveStandings(_countsByTeam, _eliminated, results);
    }

    /// <summary>
    /// 이 세션이 직접 등록한 두 bus 구독만 해제한다. 중복 호출에 안전하다.
    /// 세션이 획득하지 않은 채널까지 지우는 ClearAll은 소유 범위를 벗어나므로 호출하지 않는다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EventManager.GetSubscriber<CrowdCountChangedEvent>().Unsubscribe(_onCrowdCountChanged);
        EventManager.GetSubscriber<CrowdEliminatedEvent>().Unsubscribe(_onCrowdEliminated);
    }

#if UNITY_EDITOR
    /// <summary>
    /// play-smoke 검증용으로 남은 시간을 강제로 설정한다. 음수는 0으로 고정한다.
    /// </summary>
    public void DebugSetTimeRemaining(float seconds)
    {
        TimeRemaining = seconds < 0f ? 0f : seconds;
    }
#endif

    // bus handler: 이미 일어난 인원 수 변경을 반영만 한다. 절대 던지지 않도록 범위를 검사하고 상태를 변경하지 않는다.
    private void OnCrowdCountChanged(CrowdCountChangedEvent e)
    {
        if (State == MatchState.Finished)
        {
            return;
        }

        if ((uint)e.CrowdId >= (uint)_countsByTeam.Length)
        {
            return;
        }

        _countsByTeam[e.CrowdId] = e.MemberCount;
    }

    // bus handler: 제거 사실을 반영하고 종료 조건을 평가한다. player 제거는 같은 batch의 rival 제거보다
    // 항상 먼저 도착하므로(발행 순서 계약) 여기서 먼저 Finish되면 패배가 우선 확정된다.
    private void OnCrowdEliminated(CrowdEliminatedEvent e)
    {
        if (State == MatchState.Finished)
        {
            return;
        }

        if ((uint)e.CrowdId >= (uint)_eliminated.Length)
        {
            return;
        }

        _eliminated[e.CrowdId] = true;
        _countsByTeam[e.CrowdId] = 0;

        if (e.CrowdId == MatchRules.PlayerTeam)
        {
            // 패배: player가 제거 대상에서 빠지므로 ResolveWinner는 남은 crowd 중 최대 인원 팀을 반환한다.
            Finish(MatchRules.ResolveWinner(_countsByTeam, _eliminated));
            return;
        }

        if (AllRivalsEliminated())
        {
            // 승리: 제거되지 않은 팀은 player뿐이므로 ResolveWinner는 player를 반환한다.
            Finish(MatchRules.ResolveWinner(_countsByTeam, _eliminated));
        }
    }

    // idempotent 종료: 첫 호출만 상태를 전이하고 StateChanged를 발생시킨다.
    private void Finish(int winnerTeam)
    {
        if (State == MatchState.Finished)
        {
            return;
        }

        State = MatchState.Finished;
        WinnerTeam = winnerTeam;
        PlayerWon = winnerTeam == MatchRules.PlayerTeam;
        StateChanged?.Invoke(MatchState.Finished);
    }

    private bool AllRivalsEliminated()
    {
        for (int i = 1; i < _eliminated.Length; i++)
        {
            if (!_eliminated[i])
            {
                return false;
            }
        }

        return true;
    }
}
