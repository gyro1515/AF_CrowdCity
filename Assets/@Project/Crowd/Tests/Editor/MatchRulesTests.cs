using System.Collections.Generic;
using NUnit.Framework;

/// <summary>
/// <see cref="MatchRules"/>의 순위 정렬과 승자 판정(동률 시 플레이어 우선, 전원 탈락 시 -1)을 검증하는 EditMode 테스트다.
/// </summary>
[TestFixture]
public sealed class MatchRulesTests
{
    /// <summary>
    /// 탈락하지 않은 팀을 인원 내림차순(동률은 낮은 팀 우선)으로 앞에, 탈락 팀을 인원 0으로 뒤에(팀 오름차순) 정렬함을 검증한다.
    /// </summary>
    [Test]
    public void ResolveStandings_SortsByCountThenTeam_EliminatedLast()
    {
        int[] counts = { 3, 5, 1, 5 };
        bool[] eliminated = { false, false, true, false };
        List<CrowdStanding> results = new List<CrowdStanding>();

        MatchRules.ResolveStandings(counts, eliminated, results);

        Assert.AreEqual(4, results.Count);
        Assert.AreEqual(1, results[0].Team); // 5명, 낮은 팀
        Assert.AreEqual(3, results[1].Team); // 5명
        Assert.AreEqual(0, results[2].Team); // 3명
        Assert.AreEqual(2, results[3].Team); // 탈락

        Assert.IsTrue(results[3].Eliminated);
        Assert.AreEqual(0, results[3].MemberCount, "탈락 팀 인원은 0으로 보고");
        Assert.AreEqual(5, results[0].MemberCount);
    }

    /// <summary>
    /// 전원 탈락이면 모든 항목이 인원 0, 탈락 표시로 팀 오름차순 배치됨을 검증한다.
    /// </summary>
    [Test]
    public void ResolveStandings_AllEliminated_TeamAscendingZeroCounts()
    {
        int[] counts = { 4, 9, 2, 7 };
        bool[] eliminated = { true, true, true, true };
        List<CrowdStanding> results = new List<CrowdStanding>();

        MatchRules.ResolveStandings(counts, eliminated, results);

        Assert.AreEqual(4, results.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(i, results[i].Team);
            Assert.IsTrue(results[i].Eliminated);
            Assert.AreEqual(0, results[i].MemberCount);
        }
    }

    /// <summary>
    /// 정렬 결과가 넘겨받은 리스트를 먼저 비우고 채움을 검증한다.
    /// </summary>
    [Test]
    public void ResolveStandings_ClearsResults()
    {
        int[] counts = { 1, 2, 3, 4 };
        bool[] eliminated = { false, false, false, false };
        List<CrowdStanding> results = new List<CrowdStanding>
        {
            new CrowdStanding(99, 99, false),
        };

        MatchRules.ResolveStandings(counts, eliminated, results);

        Assert.AreEqual(4, results.Count);
    }

    /// <summary>
    /// 최고 인원 동률에 플레이어(team0)가 포함되면 플레이어가 승자가 됨을 검증한다.
    /// </summary>
    [Test]
    public void ResolveWinner_TieIncludingPlayer_FavorsPlayer()
    {
        int[] counts = { 5, 5, 3, 2 };
        bool[] eliminated = { false, false, false, false };

        Assert.AreEqual(MatchRules.PlayerTeam, MatchRules.ResolveWinner(counts, eliminated));
    }

    /// <summary>
    /// 플레이어가 없는 최고 인원 동률이면 가장 낮은 팀 id가 승자가 됨을 검증한다.
    /// </summary>
    [Test]
    public void ResolveWinner_TieWithoutPlayer_FavorsLowestTeam()
    {
        int[] counts = { 3, 5, 5, 2 };
        bool[] eliminated = { false, false, false, false };

        Assert.AreEqual(1, MatchRules.ResolveWinner(counts, eliminated));
    }

    /// <summary>
    /// 탈락한 팀은 인원이 많아도 승자에서 제외됨을 검증한다.
    /// </summary>
    [Test]
    public void ResolveWinner_ExcludesEliminated()
    {
        int[] counts = { 7, 3, 2, 1 };
        bool[] eliminated = { true, false, false, false };

        Assert.AreEqual(1, MatchRules.ResolveWinner(counts, eliminated));
    }

    /// <summary>
    /// 단독 최고 인원 팀이 그대로 승자가 됨을 검증한다.
    /// </summary>
    [Test]
    public void ResolveWinner_ClearLeader()
    {
        int[] counts = { 2, 9, 1, 1 };
        bool[] eliminated = { false, false, false, false };

        Assert.AreEqual(1, MatchRules.ResolveWinner(counts, eliminated));
    }

    /// <summary>
    /// 전원 탈락이면 승자가 없어 -1을 반환함을 검증한다.
    /// </summary>
    [Test]
    public void ResolveWinner_AllEliminated_ReturnsMinusOne()
    {
        int[] counts = { 4, 5, 6, 7 };
        bool[] eliminated = { true, true, true, true };

        Assert.AreEqual(-1, MatchRules.ResolveWinner(counts, eliminated));
    }
}
