using System;
using System.Collections.Generic;

/// <summary>
/// 매치 진행 상태를 나타낸다. Ready(입력 대기) -> Playing(진행) -> Finished(종료) 순서로만 전이한다.
/// </summary>
public enum MatchState
{
    /// <summary>
    /// 월드가 정지된 상태로 첫 드래그 입력을 기다린다.
    /// </summary>
    Ready,

    /// <summary>
    /// 타이머가 감소하고 시뮬레이션이 진행 중이다.
    /// </summary>
    Playing,

    /// <summary>
    /// 매치가 종료되어 승자와 최종 순위가 확정됐다.
    /// </summary>
    Finished,
}

/// <summary>
/// HUD 같은 경계 소비자가 세션 상태를 읽기 전용으로 관찰하는 인터페이스다.
/// 상태 변경 진입점은 노출하지 않는다.
/// </summary>
public interface IGameSessionReadOnly
{
    /// <summary>
    /// 현재 매치 상태를 반환한다.
    /// </summary>
    MatchState State { get; }

    /// <summary>
    /// 남은 매치 시간을 초 단위로 반환한다.
    /// </summary>
    float TimeRemaining { get; }

    /// <summary>
    /// 승리한 팀 id를 반환한다. <see cref="MatchState.Finished"/> 이전에는 -1이다.
    /// </summary>
    int WinnerTeam { get; }

    /// <summary>
    /// 플레이어(팀 0)가 승리했는지 반환한다. <see cref="MatchState.Finished"/> 이전에는 false다.
    /// </summary>
    bool PlayerWon { get; }

    /// <summary>
    /// 현재 순위를 results에 채운다. 호출자가 재사용하는 List를 받아 먼저 비운 뒤 채운다.
    /// </summary>
    void GetStandings(List<CrowdStanding> results);

    /// <summary>
    /// 매치 상태가 전이될 때마다 새 상태와 함께 발생한다. 전이당 한 번만 발생한다.
    /// 구독한 lifecycle과 같은 lifecycle에서 반드시 해제한다.
    /// </summary>
    event Action<MatchState> StateChanged;
}
