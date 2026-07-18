using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// <see cref="RecruitResolver"/>의 결정적 영입 판정(가장 가까운 claimant, 거리 동률 시 낮은 Id)을 검증하는 EditMode 테스트다.
/// </summary>
[TestFixture]
public sealed class RecruitResolverTests
{
    private const int Cap = 256;
    private const float Radius = 1.2f;

    private static Vector2 V(float x, float z)
    {
        return new Vector2(x, z);
    }

    private static SpatialGrid NewGrid(AgentBuffer buffer)
    {
        SpatialGrid grid = new SpatialGrid(1.5f, Cap);
        grid.Rebuild(buffer);
        return grid;
    }

    private static int ToTeamOf(AgentBuffer buffer, List<RecruitAssignment> results, int neutralId)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (buffer.Id[results[i].AgentIndex] == neutralId)
            {
                return results[i].ToTeam;
            }
        }

        return int.MinValue;
    }

    /// <summary>
    /// 반경 안에 member가 하나뿐이면 그 팀으로 합류하고, 반경 밖이면 어떤 claim도 생기지 않음을 검증한다.
    /// </summary>
    [Test]
    public void SingleClaimant_WithinRadius_Joins_OutsideRadius_Ignored()
    {
        RecruitResolver resolver = new RecruitResolver();
        List<RecruitAssignment> results = new List<RecruitAssignment>();

        // 반경 안.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
            buffer.Add(1, 0, true, V(0.5f, 0f));
            SpatialGrid grid = NewGrid(buffer);

            resolver.Resolve(buffer, grid, Radius, 1f, results);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(0, results[0].ToTeam);
            Assert.AreEqual(0, buffer.Id[results[0].AgentIndex]);
        }

        // 반경 밖.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
            buffer.Add(1, 0, true, V(5f, 0f));
            SpatialGrid grid = NewGrid(buffer);

            resolver.Resolve(buffer, grid, Radius, 1f, results);
            Assert.AreEqual(0, results.Count);
        }
    }

    /// <summary>
    /// 경합 시 더 가까운 member의 팀으로 합류함을 검증한다(순수 거리 판정).
    /// </summary>
    [Test]
    public void Contested_NearestClaimantWins()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
        buffer.Add(1, 0, true, V(0.5f, 0f));  // 거리 0.5
        buffer.Add(2, 1, true, V(0.3f, 0f));  // 거리 0.3 (더 가까움)
        SpatialGrid grid = NewGrid(buffer);

        RecruitResolver resolver = new RecruitResolver();
        List<RecruitAssignment> results = new List<RecruitAssignment>();
        resolver.Resolve(buffer, grid, Radius, 1f, results);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(1, ToTeamOf(buffer, results, 0), "가까운 team1로 합류");
    }

    /// <summary>
    /// 거리가 동률이면 claimant Id가 낮은 쪽이 이김을 검증한다(위치/삽입 순서와 무관).
    /// </summary>
    [Test]
    public void Contested_DistanceTie_LowerIdWins()
    {
        // team0 claimant의 Id가 더 낮은 경우 -> team0 승.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
            buffer.Add(1, 0, true, V(0.5f, 0f));   // Id 1, 거리 0.5
            buffer.Add(2, 1, true, V(-0.5f, 0f));  // Id 2, 거리 0.5 (동률)
            SpatialGrid grid = NewGrid(buffer);

            RecruitResolver resolver = new RecruitResolver();
            List<RecruitAssignment> results = new List<RecruitAssignment>();
            resolver.Resolve(buffer, grid, Radius, 1f, results);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(0, ToTeamOf(buffer, results, 0), "동률이면 낮은 Id(1)의 team0");
        }

        // claimant Id를 뒤집으면(team1이 더 낮은 Id) -> team1 승. 결과가 Id 기준임을 확인.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
            buffer.Add(9, 0, true, V(0.5f, 0f));   // Id 9, 거리 0.5
            buffer.Add(2, 1, true, V(-0.5f, 0f));  // Id 2, 거리 0.5 (동률, 더 낮은 Id)
            SpatialGrid grid = NewGrid(buffer);

            RecruitResolver resolver = new RecruitResolver();
            List<RecruitAssignment> results = new List<RecruitAssignment>();
            resolver.Resolve(buffer, grid, Radius, 1f, results);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(1, ToTeamOf(buffer, results, 0), "동률이면 낮은 Id(2)의 team1");
        }
    }

    /// <summary>
    /// 같은 시나리오에서 member 삽입 순서를 뒤집어도 동률 판정 결과가 동일함(grid 순서 무관)을 검증한다.
    /// </summary>
    [Test]
    public void Contested_Tie_IsInsertionOrderIndependent()
    {
        int forwardResult;
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
            buffer.Add(3, 0, true, V(0.5f, 0f));
            buffer.Add(7, 1, true, V(-0.5f, 0f));
            SpatialGrid grid = NewGrid(buffer);
            RecruitResolver resolver = new RecruitResolver();
            List<RecruitAssignment> results = new List<RecruitAssignment>();
            resolver.Resolve(buffer, grid, Radius, 1f, results);
            forwardResult = ToTeamOf(buffer, results, 0);
        }

        int reversedResult;
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(7, 1, true, V(-0.5f, 0f));
            buffer.Add(3, 0, true, V(0.5f, 0f));
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
            SpatialGrid grid = NewGrid(buffer);
            RecruitResolver resolver = new RecruitResolver();
            List<RecruitAssignment> results = new List<RecruitAssignment>();
            resolver.Resolve(buffer, grid, Radius, 1f, results);
            reversedResult = ToTeamOf(buffer, results, 0);
        }

        Assert.AreEqual(0, forwardResult, "낮은 Id(3)의 team0가 이겨야 한다");
        Assert.AreEqual(forwardResult, reversedResult, "삽입 순서와 무관하게 동일 결과여야 한다");
    }

    /// <summary>
    /// 여러 중립이 있으면 각 중립마다 정확히 하나의 claim이 생기고, 각자 가장 가까운 팀으로 합류함을 검증한다.
    /// </summary>
    [Test]
    public void MultipleNeutrals_OneClaimEach()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));    // team0 근처
        buffer.Add(1, AgentBuffer.NeutralTeam, false, V(10f, 0f));  // team1 근처
        buffer.Add(2, AgentBuffer.NeutralTeam, false, V(50f, 0f));  // 아무도 없음
        buffer.Add(3, 0, true, V(0.4f, 0f));
        buffer.Add(4, 1, true, V(10.3f, 0f));
        SpatialGrid grid = NewGrid(buffer);

        RecruitResolver resolver = new RecruitResolver();
        List<RecruitAssignment> results = new List<RecruitAssignment>();
        resolver.Resolve(buffer, grid, Radius, 1f, results);

        Assert.AreEqual(2, results.Count, "합류 가능한 중립은 2명");
        Assert.AreEqual(0, ToTeamOf(buffer, results, 0));
        Assert.AreEqual(1, ToTeamOf(buffer, results, 1));
        Assert.AreEqual(int.MinValue, ToTeamOf(buffer, results, 2), "고립된 중립은 claim 없음");
    }

    /// <summary>
    /// [스케일 인지] 큰 중립(또는 큰 recruiter)은 pair 평균 반경으로 base recruitRadius(1.2)를 넘는 거리에서 영입되고,
    /// 스케일 1 중립은 base 경계를 그대로 쓴다. base radius(1.2) &lt; 거리 1.35 &lt; pairR(1.2*1.25=1.5)로 대조한다.
    /// </summary>
    [Test]
    public void SizeAware_LargeNeutralOrRecruiter_RecruitedBeyondBaseRadius()
    {
        const float MaxScale = 1.5f;

        // (a) 큰 중립(스케일 1.5): pairR = 1.2*0.5*(1.5+1.0)=1.5 > 1.35 => 영입된다.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f), 1.5f);
            buffer.Add(1, 0, true, V(1.35f, 0f)); // 스케일 1 recruiter, 거리 1.35.
            SpatialGrid grid = NewGrid(buffer);

            RecruitResolver resolver = new RecruitResolver();
            List<RecruitAssignment> results = new List<RecruitAssignment>();
            resolver.Resolve(buffer, grid, Radius, MaxScale, results);

            Assert.AreEqual(1, results.Count, "큰 중립(1.5)은 base radius를 넘는 1.35 거리에서 영입된다");
            Assert.AreEqual(0, ToTeamOf(buffer, results, 0), "team0로 합류");
        }

        // (b) 대조: 스케일 1 중립은 pairR = 1.2 < 1.35 => 영입되지 않는다(base 경계 유지).
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
            buffer.Add(1, 0, true, V(1.35f, 0f));
            SpatialGrid grid = NewGrid(buffer);

            RecruitResolver resolver = new RecruitResolver();
            List<RecruitAssignment> results = new List<RecruitAssignment>();
            resolver.Resolve(buffer, grid, Radius, MaxScale, results);

            Assert.AreEqual(0, results.Count, "스케일 1 중립은 base radius(1.2) 밖 1.35 거리에서 영입되지 않는다");
        }

        // (c) 큰 recruiter: 스케일 1 중립도 스케일 1.5 member에게는 pairR = 1.5 > 1.35 => 영입된다(recruiter 쪽 스케일도 도달 거리를 늘린다).
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, AgentBuffer.NeutralTeam, false, V(0f, 0f));
            buffer.Add(1, 0, false, V(1.35f, 0f), 1.5f); // 스케일 1.5 recruiter(영입된 큰 member는 최대 스케일까지 가능).
            SpatialGrid grid = NewGrid(buffer);

            RecruitResolver resolver = new RecruitResolver();
            List<RecruitAssignment> results = new List<RecruitAssignment>();
            resolver.Resolve(buffer, grid, Radius, MaxScale, results);

            Assert.AreEqual(1, results.Count, "스케일 1.5 recruiter는 1.35 거리 중립을 영입한다");
            Assert.AreEqual(0, ToTeamOf(buffer, results, 0), "team0로 합류");
        }
    }
}
