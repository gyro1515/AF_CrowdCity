using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// <see cref="CombatResolver"/>의 전향 누적 수식, 스냅샷 순수성, 제거 판정, 순열 불변성을 검증하는 EditMode 테스트다.
/// 모든 시나리오는 고정 위치와 고정 seed를 써서 결정적으로 동작한다.
/// </summary>
[TestFixture]
public sealed class CombatResolverTests
{
    private const int Cap = 256;
    private const int TeamCount = 4;

    private static Vector2 V(float x, float z)
    {
        return new Vector2(x, z);
    }

    private static SimTuning Default()
    {
        SimTuning tuning;
        tuning.RecruitRadius = 1.2f;
        tuning.CombatRadius = 1.0f;
        tuning.ConvertPerSecond = 10f;
        tuning.PairNormalizer = 8;
        return tuning;
    }

    private static SpatialGrid NewGrid(AgentBuffer buffer)
    {
        SpatialGrid grid = new SpatialGrid(1.5f, Cap);
        grid.Rebuild(buffer);
        return grid;
    }

    /// <summary>
    /// 접촉 pair가 PairNormalizer를 초과하면 clamp가 1.0에서 포화하고, 누적값이 dt마다 쌓여 예상한 호출에서만 전향이 방출됨을 검증한다.
    /// </summary>
    [Test]
    public void ConversionAccumulator_ClampSaturates_ConvertsOnFourthCall()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        // team0: 6명(리더 1 + 팔로워 5), 반경 안에 밀집.
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.10f, 0.00f));
        buffer.Add(2, 0, false, V(0.20f, 0.00f));
        buffer.Add(3, 0, false, V(0.00f, 0.10f));
        buffer.Add(4, 0, false, V(0.10f, 0.10f));
        buffer.Add(5, 0, false, V(0.20f, 0.10f));
        // team1: 5명(리더 1 + 팔로워 4), 반경 안에 밀집.
        buffer.Add(6, 1, true, V(0.00f, 0.20f));
        buffer.Add(7, 1, false, V(0.10f, 0.20f));
        buffer.Add(8, 1, false, V(0.20f, 0.20f));
        buffer.Add(9, 1, false, V(0.00f, 0.30f));
        buffer.Add(10, 1, false, V(0.10f, 0.30f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = Default();

        // clamp01(30/8)=1.0, rate=10, dt=0.03 -> 0.3/호출. 3호출까지 누적 0.9(<1) => 전향 없음.
        for (int call = 1; call <= 3; call++)
        {
            resolver.Resolve(buffer, grid, tuning, 0.03f, state, outcome);
            Assert.AreEqual(0, outcome.Conversions.Count, "call " + call + " conversions");
            Assert.AreEqual(0, outcome.Eliminations.Count, "call " + call + " eliminations");
        }

        // 4호출째 누적 1.2(>=1) => 팔로워 1명이 team0으로 전향. (미포화라면 첫 호출에 이미 전향했을 것)
        resolver.Resolve(buffer, grid, tuning, 0.03f, state, outcome);
        Assert.AreEqual(1, outcome.Conversions.Count);
        Assert.AreEqual(0, outcome.Eliminations.Count);

        CrowdConversion converted = outcome.Conversions[0];
        Assert.AreEqual(1, buffer.Team[converted.AgentIndex], "victim은 team1 소속이어야 한다");
        Assert.IsFalse(buffer.IsLeader[converted.AgentIndex], "victim은 리더가 아니어야 한다");
        Assert.AreEqual(0, converted.ToTeam, "전향 대상 팀은 team0");
    }

    /// <summary>
    /// 접촉 pair가 PairNormalizer 미만이면 clamp가 pairs/normalizer 비율로 스케일되어 누적 속도가 느려짐을 검증한다.
    /// </summary>
    [Test]
    public void ConversionRate_FractionalClamp_ConvertsOnSecondCall()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        // team0(승자): 4명 원점 밀집.
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.05f, 0.00f));
        buffer.Add(2, 0, false, V(0.00f, 0.05f));
        buffer.Add(3, 0, false, V(0.05f, 0.05f));
        // team1(패자): 반경 안 팔로워 1 + 멀리 떨어진 리더/팔로워 => 접촉 pair는 4개(=team0 전원 x 근접 팔로워 1).
        buffer.Add(4, 1, false, V(0.50f, 0.00f));
        buffer.Add(5, 1, true, V(30.0f, 0.00f));
        buffer.Add(6, 1, false, V(30.0f, 0.50f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = Default();

        // clamp01(4/8)=0.5, rate=10, dt=0.12 -> 0.6/호출. 1호출 0.6(<1) => 전향 없음.
        resolver.Resolve(buffer, grid, tuning, 0.12f, state, outcome);
        Assert.AreEqual(0, outcome.Conversions.Count, "clamp가 0.5면 첫 호출엔 전향이 없어야 한다");

        // 2호출째 1.2(>=1) => 근접 팔로워(id 4)만 team0으로 전향.
        resolver.Resolve(buffer, grid, tuning, 0.12f, state, outcome);
        Assert.AreEqual(1, outcome.Conversions.Count);
        Assert.AreEqual(0, outcome.Eliminations.Count);
        Assert.AreEqual(4, buffer.Id[outcome.Conversions[0].AgentIndex]);
        Assert.AreEqual(0, outcome.Conversions[0].ToTeam);
    }

    /// <summary>
    /// 접촉이 끊긴 호출에서 해당 pair 누적값이 0으로 리셋되어, 재접촉 후 다시 처음부터 쌓임을 검증한다.
    /// </summary>
    [Test]
    public void Accumulator_ResetsWhenContactBreaks()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        // team0: 5명.
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.10f, 0.00f));
        buffer.Add(2, 0, false, V(0.20f, 0.00f));
        buffer.Add(3, 0, false, V(0.00f, 0.10f));
        buffer.Add(4, 0, false, V(0.10f, 0.10f));
        // team1: 4명(리더 1 + 팔로워 3). 인덱스 5..8.
        buffer.Add(5, 1, true, V(0.00f, 0.20f));
        buffer.Add(6, 1, false, V(0.10f, 0.20f));
        buffer.Add(7, 1, false, V(0.20f, 0.20f));
        buffer.Add(8, 1, false, V(0.00f, 0.30f));

        Vector2[] team1Original = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            team1Original[i] = buffer.Pos[5 + i];
        }

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = Default();

        // clamp01(20/8)=1.0, rate=10, dt=0.03 -> 0.3/호출.
        // 접촉 3회: 누적 0.9(<1).
        for (int call = 1; call <= 3; call++)
        {
            resolver.Resolve(buffer, grid, tuning, 0.03f, state, outcome);
            Assert.AreEqual(0, outcome.Conversions.Count, "contact call " + call);
        }

        // 접촉 끊김: team1을 멀리 이동 -> pair 0 -> 누적 리셋 0.
        for (int i = 0; i < 4; i++)
        {
            buffer.Pos[5 + i] = V(100f + i * 0.1f, 100f);
        }
        grid.Rebuild(buffer);
        resolver.Resolve(buffer, grid, tuning, 0.03f, state, outcome);
        Assert.AreEqual(0, outcome.Conversions.Count, "no-contact call");

        // 재접촉: 원위치 복귀. 리셋되었으므로 이 호출은 누적 0.3(<1) => 전향 없음.
        // (리셋이 없었다면 0.9+0.3=1.2로 전향이 났을 것)
        for (int i = 0; i < 4; i++)
        {
            buffer.Pos[5 + i] = team1Original[i];
        }
        grid.Rebuild(buffer);
        resolver.Resolve(buffer, grid, tuning, 0.03f, state, outcome);
        Assert.AreEqual(0, outcome.Conversions.Count, "reset proof call (0.3 < 1)");

        // 이후 접촉 2회 더: 0.6, 0.9 -> 전향 없음.
        resolver.Resolve(buffer, grid, tuning, 0.03f, state, outcome);
        Assert.AreEqual(0, outcome.Conversions.Count);
        resolver.Resolve(buffer, grid, tuning, 0.03f, state, outcome);
        Assert.AreEqual(0, outcome.Conversions.Count);

        // 8번째 접촉 상당(리셋 후 4번째): 누적 1.2 -> 전향 1건.
        resolver.Resolve(buffer, grid, tuning, 0.03f, state, outcome);
        Assert.AreEqual(1, outcome.Conversions.Count, "machinery still emits after rebuild");
    }

    /// <summary>
    /// 두 crowd의 시작 인원이 같으면 어느 방향으로도 전향/제거가 일어나지 않고 계속 inert함을 검증한다.
    /// </summary>
    [Test]
    public void EqualSize_Inert()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        // team0 3명, team1 3명 밀집(동수).
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.10f, 0.00f));
        buffer.Add(2, 0, false, V(0.20f, 0.00f));
        buffer.Add(3, 1, true, V(0.00f, 0.10f));
        buffer.Add(4, 1, false, V(0.10f, 0.10f));
        buffer.Add(5, 1, false, V(0.20f, 0.10f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = Default();

        for (int call = 0; call < 50; call++)
        {
            resolver.Resolve(buffer, grid, tuning, 0.1f, state, outcome);
            Assert.AreEqual(0, outcome.Conversions.Count, "call " + call);
            Assert.AreEqual(0, outcome.Eliminations.Count, "call " + call);
        }
    }

    /// <summary>
    /// 시작 인원 우열이 뒤집히면 전향 방향도 뒤집힘을 검증한다.
    /// </summary>
    [Test]
    public void FlowFlips_WhenSizesCross()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        // team0 5명(리더 id0 + 팔로워 id1..4), team1 3명(리더 id5 + 팔로워 id6,7). 밀집.
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.10f, 0.00f));
        buffer.Add(2, 0, false, V(0.20f, 0.00f));
        buffer.Add(3, 0, false, V(0.00f, 0.10f));
        buffer.Add(4, 0, false, V(0.10f, 0.10f));
        buffer.Add(5, 1, true, V(0.20f, 0.10f));
        buffer.Add(6, 1, false, V(0.00f, 0.20f));
        buffer.Add(7, 1, false, V(0.10f, 0.20f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = Default();

        // Phase A: team0(5) > team1(3) -> team1 팔로워가 team0으로 전향.
        CombatState stateA = new CombatState(TeamCount);
        CrowdConversion firstA = ResolveUntilFirstConversion(resolver, buffer, grid, tuning, 0.03f, stateA, outcome, 8);
        Assert.AreEqual(0, firstA.ToTeam, "team0가 클 때는 team0으로 전향");
        Assert.AreEqual(1, buffer.Team[firstA.AgentIndex], "victim은 team1");

        // Phase B: 팔로워 2명을 team1으로 옮겨 우열을 뒤집는다. team0(3) < team1(5).
        buffer.Team[1] = 1;
        buffer.Team[2] = 1;
        grid.Rebuild(buffer);

        CombatState stateB = new CombatState(TeamCount);
        CrowdConversion firstB = ResolveUntilFirstConversion(resolver, buffer, grid, tuning, 0.03f, stateB, outcome, 8);
        Assert.AreEqual(1, firstB.ToTeam, "team1이 클 때는 team1으로 전향");
        Assert.AreEqual(0, buffer.Team[firstB.AgentIndex], "victim은 이제 team0");
    }

    /// <summary>
    /// 3방향 접촉에서 같은 입력은 항상 같은 결과를 내고, agent 삽입 순서(정순/역순/셔플)를 바꿔도 Id 기준 결과가 동일함을 검증한다.
    /// </summary>
    [Test]
    public void ThreeWayContact_Deterministic_And_PermutationInvariant()
    {
        Spec[] specs =
        {
            S(0, 0, true, 0.00f, 0.00f),
            S(1, 0, false, 0.10f, 0.00f),
            S(2, 0, false, 0.20f, 0.00f),
            S(3, 0, false, 0.30f, 0.00f),
            S(4, 1, true, 0.00f, 0.10f),
            S(5, 1, false, 0.10f, 0.10f),
            S(6, 1, false, 0.20f, 0.10f),
            S(7, 2, true, 0.00f, 0.20f),
            S(8, 2, false, 0.10f, 0.20f),
        };

        int n = specs.Length;
        int[] canonical = new int[n];
        int[] reversed = new int[n];
        for (int i = 0; i < n; i++)
        {
            canonical[i] = i;
            reversed[i] = n - 1 - i;
        }
        int[] shuffled = Shuffle(canonical, 987654321);

        string sigCanonical = RunScenario(specs, canonical, 0.3f);
        string sigCanonicalAgain = RunScenario(specs, canonical, 0.3f);
        string sigReversed = RunScenario(specs, reversed, 0.3f);
        string sigShuffled = RunScenario(specs, shuffled, 0.3f);

        Assert.IsTrue(sigCanonical.Contains("->"), "시나리오는 최소 한 건의 전향을 만들어야 한다: " + sigCanonical);
        Assert.AreEqual(sigCanonical, sigCanonicalAgain, "동일 입력은 동일 결과여야 한다");
        Assert.AreEqual(sigCanonical, sigReversed, "역순(팀 역순 포함) 삽입도 같은 결과여야 한다");
        Assert.AreEqual(sigCanonical, sigShuffled, "셔플 삽입도 같은 결과여야 한다");
    }

    /// <summary>
    /// 홀로 남은 리더는 접촉한 더 큰 팀 중 가장 가까운 member를 가진 팀에게 제거됨(nearest 규칙)을 검증한다.
    /// </summary>
    [Test]
    public void Elimination_Attribution_NearestWins()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        // team0: 홀로 남은 리더.
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        // team1: 3명. 근접 member는 거리 0.5.
        buffer.Add(10, 1, true, V(5.0f, 5.0f));
        buffer.Add(11, 1, false, V(5.0f, 6.0f));
        buffer.Add(12, 1, false, V(0.5f, 0.0f));
        // team2: 3명. 근접 member는 거리 0.4(더 가까움).
        buffer.Add(20, 2, true, V(-5.0f, 5.0f));
        buffer.Add(21, 2, false, V(-5.0f, 6.0f));
        buffer.Add(22, 2, false, V(0.4f, 0.0f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        Assert.AreEqual(1, outcome.Eliminations.Count);
        CrowdElimination elim = outcome.Eliminations[0];
        Assert.AreEqual(0, elim.Team, "제거된 팀은 team0");
        Assert.AreEqual(2, elim.ByTeam, "더 가까운 team2가 제거");
        Assert.AreEqual(0, buffer.Id[elim.LeaderAgentIndex]);
        // 제거된 리더는 eliminator 팀으로의 전향도 함께 방출된다.
        Assert.IsTrue(ContainsConversion(buffer, outcome, 0, 2));
    }

    /// <summary>
    /// 제거 후보 팀이 같은 거리로 접촉하면 낮은 팀 id가 eliminator가 됨(tie -> lower team)을 검증한다.
    /// </summary>
    [Test]
    public void Elimination_Attribution_TieFavorsLowerTeam()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        // team1 근접 member (0, 0.4): 거리 0.4.
        buffer.Add(10, 1, true, V(5.0f, 5.0f));
        buffer.Add(11, 1, false, V(5.0f, 6.0f));
        buffer.Add(12, 1, false, V(0.0f, 0.4f));
        // team2 근접 member (0.4, 0): 거리 0.4(동률).
        buffer.Add(20, 2, true, V(-5.0f, 5.0f));
        buffer.Add(21, 2, false, V(-5.0f, 6.0f));
        buffer.Add(22, 2, false, V(0.4f, 0.0f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        Assert.AreEqual(1, outcome.Eliminations.Count);
        Assert.AreEqual(0, outcome.Eliminations[0].Team);
        Assert.AreEqual(1, outcome.Eliminations[0].ByTeam, "동률이면 낮은 팀 id(team1)가 제거");
    }

    /// <summary>
    /// 홀로 남은 리더 둘(1 대 1)은 서로 strictly 크지 않으므로 아무 일도 일어나지 않음을 검증한다.
    /// </summary>
    [Test]
    public void BothLoneLeaders_Inert()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        buffer.Add(1, 1, true, V(0.5f, 0.0f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        Assert.AreEqual(0, outcome.Conversions.Count);
        Assert.AreEqual(0, outcome.Eliminations.Count);
    }

    /// <summary>
    /// 홀로 남은 리더는 strictly 더 큰 팀에게만 제거되고, 동수(1 대 1) 이웃에게는 제거되지 않음을 검증한다.
    /// </summary>
    [Test]
    public void LoneLeader_EliminatedOnlyVsStrictlyLarger()
    {
        // (a) team1이 2명(> 1)이면 team0 리더가 제거된다.
        {
            AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));
            buffer.Add(1, 1, true, V(5.0f, 5.0f));
            buffer.Add(2, 1, false, V(0.5f, 0.0f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);
            resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

            Assert.AreEqual(1, outcome.Eliminations.Count, "size 2 이웃은 제거 가능");
            Assert.AreEqual(0, outcome.Eliminations[0].Team);
            Assert.AreEqual(1, outcome.Eliminations[0].ByTeam);
        }

        // (b) team1이 1명(홀로)이면 제거되지 않는다.
        {
            AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));
            buffer.Add(1, 1, true, V(0.5f, 0.0f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);
            resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

            Assert.AreEqual(0, outcome.Eliminations.Count, "동수 이웃은 제거 불가");
        }
    }

    /// <summary>
    /// 여러 승자 팀이 같은 victim을 노리면 가장 가까운 팀이 가져가고, 밀린 팀의 예산은 소모되지 않고 다음 호출로 이월됨을 검증한다.
    /// </summary>
    [Test]
    public void VictimClaimableByTwoTeams_NearestWins_OtherBudgetRetained()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        // team2(패자): victim v(id 100)는 원점, 리더(id 101)는 멀리.
        buffer.Add(100, 2, false, V(0.0f, 0.0f));
        buffer.Add(101, 2, true, V(40.0f, 40.0f));
        // team0(승자 A): v에 거리 ~0.3로 더 가까운 4명 밀집.
        buffer.Add(0, 0, true, V(0.30f, 0.00f));
        buffer.Add(1, 0, false, V(0.35f, 0.00f));
        buffer.Add(2, 0, false, V(0.30f, 0.05f));
        buffer.Add(3, 0, false, V(0.35f, 0.05f));
        // team1(승자 B): v에 거리 ~0.6로 더 먼 4명 밀집.
        buffer.Add(10, 1, true, V(0.00f, 0.60f));
        buffer.Add(11, 1, false, V(0.05f, 0.60f));
        buffer.Add(12, 1, false, V(0.00f, 0.65f));
        buffer.Add(13, 1, false, V(0.05f, 0.65f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = Default();

        // pairs(0,2)=pairs(1,2)=4 -> clamp 0.5, rate 5, dt=0.06 -> 0.3/호출(<1).
        // Phase 1: 6호출. v는 매번 team0(더 가까움)에 배정 -> (1,2) 예산은 victim 없어 미달로 이월.
        for (int call = 0; call < 6; call++)
        {
            resolver.Resolve(buffer, grid, tuning, 0.06f, state, outcome);
            Assert.IsFalse(ContainsConversion(buffer, outcome, 100, 1),
                "team0가 v를 선점하는 동안 v는 team1으로 전향되면 안 된다 (call " + call + ")");
        }

        // Phase 2: team0을 멀리 치운다 -> v는 이제 team1에만 접촉 -> team1에 배정.
        for (int i = 0; i < buffer.Count; i++)
        {
            if (buffer.Team[i] == 0)
            {
                buffer.Pos[i] = V(200f + i, 200f);
            }
        }
        grid.Rebuild(buffer);

        // 이월된 (1,2) 누적값이 이미 >=1 이므로, 증분 0.3 한 번만으로 즉시 전향이 방출된다.
        // (미달 예산이 소모되었다면 0.3 한 번으로는 전향이 없어야 한다)
        resolver.Resolve(buffer, grid, tuning, 0.06f, state, outcome);
        Assert.IsTrue(ContainsConversion(buffer, outcome, 100, 1),
            "이월된 team1 예산 덕에 첫 접촉 호출에서 v가 team1으로 전향해야 한다");
    }

    /// <summary>
    /// 제거 pass가 방출하는 리더 전향이 같은 호출의 다른 팀 제거 판정에 되먹임되지 않음(order-free)을 검증한다.
    /// </summary>
    [Test]
    public void EliminationPass_LeaderConversions_DoNotFeedOtherEliminations()
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        // team1: 홀로 남은 리더 (0.6,0) -> team3에게 제거된다(먼저 처리되는 낮은 팀 id).
        buffer.Add(1, 1, true, V(0.6f, 0.0f));
        // team2: 홀로 남은 리더 (0,0) -> team1과만 접촉(거리 0.6), team3과는 비접촉(거리 1.4).
        //        team1 리더가 team3으로 전향되는 것이 되먹임되면 나중에 처리되는 team2가 잘못 제거될 수 있다.
        buffer.Add(2, 2, true, V(0.0f, 0.0f));
        // team3: 2명 (1.4,0),(1.6,0) -> team1과 접촉(거리 0.8).
        buffer.Add(3, 3, true, V(1.4f, 0.0f));
        buffer.Add(4, 3, false, V(1.6f, 0.0f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        // team1만 team3에게 제거된다. team2는 (되먹임이 없으므로) 제거되지 않는다.
        // team2는 team1보다 팀 id가 커서 나중에 처리되므로, 되먹임이 있었다면 여기서 걸린다.
        Assert.AreEqual(1, outcome.Eliminations.Count);
        Assert.AreEqual(1, outcome.Eliminations[0].Team);
        Assert.AreEqual(3, outcome.Eliminations[0].ByTeam);
        for (int i = 0; i < outcome.Eliminations.Count; i++)
        {
            Assert.AreNotEqual(2, outcome.Eliminations[i].Team,
                "team2는 스냅샷 기준으로 제거되면 안 된다 (되먹임 금지)");
        }
        // 방출 전향은 team1 리더의 흡수 전향 1건뿐.
        Assert.AreEqual(1, outcome.Conversions.Count);
        Assert.AreEqual(1, buffer.Id[outcome.Conversions[0].AgentIndex]);
        Assert.AreEqual(3, outcome.Conversions[0].ToTeam);
    }

    // ---- helpers ----

    private static CrowdConversion ResolveUntilFirstConversion(
        CombatResolver resolver, AgentBuffer buffer, SpatialGrid grid, SimTuning tuning,
        float dt, CombatState state, CombatOutcome outcome, int maxCalls)
    {
        for (int call = 0; call < maxCalls; call++)
        {
            resolver.Resolve(buffer, grid, tuning, dt, state, outcome);
            if (outcome.Conversions.Count > 0)
            {
                return outcome.Conversions[0];
            }
        }

        Assert.Fail("maxCalls 안에 전향이 방출되지 않았다");
        return default(CrowdConversion);
    }

    private static bool ContainsConversion(AgentBuffer buffer, CombatOutcome outcome, int agentId, int toTeam)
    {
        for (int i = 0; i < outcome.Conversions.Count; i++)
        {
            CrowdConversion c = outcome.Conversions[i];
            if (buffer.Id[c.AgentIndex] == agentId && c.ToTeam == toTeam)
            {
                return true;
            }
        }

        return false;
    }

    private struct Spec
    {
        public int Id;
        public int Team;
        public bool Leader;
        public float X;
        public float Z;
    }

    private static Spec S(int id, int team, bool leader, float x, float z)
    {
        Spec spec;
        spec.Id = id;
        spec.Team = team;
        spec.Leader = leader;
        spec.X = x;
        spec.Z = z;
        return spec;
    }

    private static int[] Shuffle(int[] source, int seed)
    {
        int[] copy = (int[])source.Clone();
        System.Random rng = new System.Random(seed);
        for (int i = copy.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            int tmp = copy[i];
            copy[i] = copy[j];
            copy[j] = tmp;
        }

        return copy;
    }

    // 주어진 삽입 순서로 시나리오를 실행하고, Id 기준으로 정규화한 결과 서명을 돌려준다.
    private static string RunScenario(Spec[] specs, int[] order, float dt)
    {
        AgentBuffer buffer = new AgentBuffer(Cap);
        for (int k = 0; k < order.Length; k++)
        {
            Spec s = specs[order[k]];
            buffer.Add(s.Id, s.Team, s.Leader, new Vector2(s.X, s.Z));
        }

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        resolver.Resolve(buffer, grid, Default(), dt, state, outcome);

        List<string> conversions = new List<string>();
        for (int i = 0; i < outcome.Conversions.Count; i++)
        {
            CrowdConversion c = outcome.Conversions[i];
            conversions.Add(buffer.Id[c.AgentIndex] + "->" + c.ToTeam);
        }

        conversions.Sort(StringComparer.Ordinal);

        List<string> eliminations = new List<string>();
        for (int i = 0; i < outcome.Eliminations.Count; i++)
        {
            CrowdElimination e = outcome.Eliminations[i];
            eliminations.Add("E:" + e.Team + ">" + e.ByTeam + "@" + buffer.Id[e.LeaderAgentIndex]);
        }

        eliminations.Sort(StringComparer.Ordinal);

        return string.Join(",", conversions) + "|" + string.Join(",", eliminations);
    }
}
