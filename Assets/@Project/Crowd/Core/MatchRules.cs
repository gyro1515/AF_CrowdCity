using System.Collections.Generic;

/// <summary>
/// 순위표 한 줄을 나타내는 불변 스냅샷이다.
/// </summary>
public readonly struct CrowdStanding
{
    /// <summary>
    /// 팀 id다. 0은 플레이어, 1..3은 라이벌이다.
    /// </summary>
    public readonly int Team;

    /// <summary>
    /// 리더를 포함한 인원 수다. 탈락한 팀은 항상 0이다.
    /// </summary>
    public readonly int MemberCount;

    /// <summary>
    /// 리더 탈락으로 크라우드가 제거되었는지 여부다.
    /// </summary>
    public readonly bool Eliminated;

    /// <summary>
    /// 순위표 한 줄을 생성한다.
    /// </summary>
    public CrowdStanding(int team, int memberCount, bool eliminated)
    {
        Team = team;
        MemberCount = memberCount;
        Eliminated = eliminated;
    }
}

/// <summary>
/// 순위 정렬과 승자 판정 규칙을 담당하는 순수 계산 유틸리티다.
/// 타이머 진행은 GameSession이 소유하고 종료 시점의 판정 수식만 여기에 둔다.
/// </summary>
public static class MatchRules
{
    /// <summary>
    /// 플레이어 팀 id다.
    /// </summary>
    public const int PlayerTeam = 0;

    /// <summary>
    /// 순위표를 계산해 <paramref name="results"/>에 채운다. <paramref name="results"/>는 먼저 비운다.
    /// 정렬 규칙: 탈락하지 않은 팀을 MemberCount 내림차순으로 먼저 배치하고, 인원 동률이면 낮은 팀 id가 앞선다.
    /// 탈락한 팀은 인원 0으로 맨 뒤에 팀 id 오름차순으로 배치한다.
    /// </summary>
    public static void ResolveStandings(int[] countsByTeam, bool[] eliminated, List<CrowdStanding> results)
    {
        results.Clear();

        for (int team = 0; team < countsByTeam.Length; team++)
        {
            bool isEliminated = eliminated[team];
            CrowdStanding standing = new CrowdStanding(
                team,
                isEliminated ? 0 : countsByTeam[team],
                isEliminated);

            // 새 항목이 엄격히 앞서는 첫 위치에 삽입한다. ComesBefore가 팀 id까지 포함한
            // 완전한 우선순위 비교이므로 삽입 순서와 무관하게 정렬 규칙이 유지된다.
            int insertAt = results.Count;
            for (int i = 0; i < results.Count; i++)
            {
                if (ComesBefore(standing, results[i]))
                {
                    insertAt = i;
                    break;
                }
            }

            results.Insert(insertAt, standing);
        }
    }

    /// <summary>
    /// 승자를 판정한다. 탈락하지 않은 팀 중 인원이 가장 많은 팀이 승자다.
    /// 동률에 플레이어가 포함되면 플레이어가, 아니면 가장 낮은 팀 id가 승자다. 전원 탈락이면 -1을 반환한다.
    /// </summary>
    public static int ResolveWinner(int[] countsByTeam, bool[] eliminated)
    {
        int winner = -1;
        int bestCount = -1;

        // PlayerTeam(0)부터 오름차순으로 보며 "초과할 때만 교체"하므로 동률이면 먼저 본 팀이 유지된다.
        // 플레이어가 동률에 포함되면 플레이어가, 아니면 가장 낮은 팀 id가 승자가 된다.
        for (int team = 0; team < countsByTeam.Length; team++)
        {
            if (eliminated[team])
            {
                continue;
            }

            if (countsByTeam[team] > bestCount)
            {
                bestCount = countsByTeam[team];
                winner = team;
            }
        }

        return winner;
    }

    // a가 b보다 순위표에서 엄격히 앞서면 true를 반환한다.
    // 탈락하지 않은 팀 우선 -> MemberCount 내림차순 -> 낮은 팀 id 순.
    // 둘 다 탈락이면 팀 id 오름차순.
    private static bool ComesBefore(CrowdStanding a, CrowdStanding b)
    {
        if (a.Eliminated != b.Eliminated)
        {
            return !a.Eliminated;
        }

        if (a.Eliminated)
        {
            return a.Team < b.Team;
        }

        if (a.MemberCount != b.MemberCount)
        {
            return a.MemberCount > b.MemberCount;
        }

        return a.Team < b.Team;
    }
}
