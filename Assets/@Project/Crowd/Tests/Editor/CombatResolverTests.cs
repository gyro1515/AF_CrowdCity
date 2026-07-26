using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// <see cref="CombatResolver"/>의 두 전향 모드(RateLimitConversion=false 접촉 즉시 전향 / true 점진 전향 누적 수식),
/// 스냅샷 순수성, 제거 판정, 순열 불변성을 검증하는 EditMode 테스트다.
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

    // 프로덕션 기본값과 동일하게 RateLimitConversion=false(접촉 즉시 전향), LeaderProtection=true(리더 보호 ON)로 둔다.
    // MaxScale=2로 두어 스케일 인지 질의가 최대 스케일 2까지 이웃을 잡게 한다(모든 유닛 스케일 1인 기존 테스트에는
    // pair별 반경 게이트가 radius로 축소되어 결과가 byte-identical이다).
    private static SimTuning Default()
    {
        SimTuning tuning;
        tuning.RecruitRadius = 1.2f;
        tuning.CombatRadius = 1.0f;
        // LeaderAloneRadius를 CombatRadius와 동일하게 두어 기존 테스트는 split 이전과 byte-identical하게 동작한다.
        tuning.LeaderAloneRadius = 1.0f;
        tuning.ConvertPerSecond = 10f;
        tuning.PairNormalizer = 8;
        tuning.MaxScale = 2f;
        tuning.RateLimitConversion = false;
        tuning.LeaderProtection = true;
        tuning.SeparationVisitBudget = 0; // 분리 조회 예산 cap OFF(기본): 전투 테스트는 예산과 무관하다.
        tuning.CombatFlatConvertRate = false; // flat 전향율 OFF(기본): 기존 접촉 pair 가중 규칙을 검증한다.
        tuning.UseDynamicConvertRate = false; // 동적 전향율 OFF(기본): 고정 ConvertPerSecond 경로를 검증한다.
        // 계수는 0으로 둔다. 토글이 OFF인데도 동적 분기를 타는 회귀가 생기면 전향율이 0이 되어, 기존 rate-limited
        // 테스트들이 "전향 0건"으로 시끄럽게 실패한다(조용히 통과하지 않는다).
        tuning.ConvertPerSecondPerMember = 0f;
        return tuning;
    }

    // LeaderAloneRadius split 검증용: 아군 호위 판정 반경만 CombatRadius(1.0)보다 넓게(2.0) 켠다. 적 접촉 반경은 그대로 CombatRadius.
    // MaxScale=1로 낮춘다: 4단계 질의가 넓혀지지 않았다면(구 CombatRadius*Max(1,MaxScale)=1.0) 거리 1.5 호위를 애초에 모으지
    // 못해 ownLocal=1로 리더가 제거된다. 질의 확대(Max(CombatRadius,LeaderAloneRadius)*Max(1,MaxScale)=2.0)가 있어야 호위가
    // 잡혀 면역이므로 이 테스트는 질의 확대 라인을 되돌리면 실패한다.
    // (Default의 MaxScale=2였다면 구 질의도 1.0*2=2.0이라 확대 여부와 무관하게 호위를 모아 테스트가 무력했다.)
    private static SimTuning WideLeaderAlone()
    {
        SimTuning tuning = Default();
        tuning.LeaderAloneRadius = 2.0f;
        tuning.MaxScale = 1f;
        return tuning;
    }

    // 기존 점진 전향(rate limit) 경로 검증용. Default()에서 토글만 켠다.
    private static SimTuning RateLimited()
    {
        SimTuning tuning = Default();
        tuning.RateLimitConversion = true;
        return tuning;
    }

    // 리더 보호 OFF(국소 동수 >= 에도 리더 제거) 경로 검증용. Default()에서 토글만 끈다.
    private static SimTuning LeaderProtectionOff()
    {
        SimTuning tuning = Default();
        tuning.LeaderProtection = false;
        return tuning;
    }

    // 동적 전향율(UseDynamicConvertRate=true) 경로 검증용. RateLimited()에서 토글을 켜고 member당 계수를 준다.
    // 동적 모드에서 ConvertPerSecond는 절대 읽히면 안 되므로 터무니없는 값으로 오염시켜 둔다: 잘못된 분기를 타면
    // 예산이 폭발해 첫 호출에 전향이 쏟아지므로, 테스트가 조용히 통과하는 대신 시끄럽게 실패한다.
    private static SimTuning DynamicRate(float perMember)
    {
        SimTuning tuning = RateLimited();
        tuning.UseDynamicConvertRate = true;
        tuning.ConvertPerSecondPerMember = perMember;
        tuning.ConvertPerSecond = 100000f;
        return tuning;
    }

    private static SpatialGrid NewGrid(AgentBuffer buffer)
    {
        SpatialGrid grid = new SpatialGrid(1.5f, Cap);
        grid.Rebuild(buffer);
        return grid;
    }

    /// <summary>
    /// [RateLimitConversion=true] 접촉 pair가 PairNormalizer를 초과하면 clamp가 1.0에서 포화하고, 누적값이 dt마다 쌓여 예상한 호출에서만 전향이 방출됨을 검증한다.
    /// </summary>
    [Test]
    public void RateLimited_ConversionAccumulator_ClampSaturates_ConvertsOnFourthCall()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0: 6명(리더 1 + 팔로워 5), 반경 안에 밀집.
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.10f, 0.00f));
        buffer.Add(2, 0, false, V(0.20f, 0.00f));
        buffer.Add(3, 0, false, V(0.00f, 0.10f));
        buffer.Add(4, 0, false, V(0.10f, 0.10f));
        buffer.Add(5, 0, false, V(0.20f, 0.10f));
        // team1: 5명(리더 1 + 팔로워 4). 리더는 반경 밖(멀리) 두어 stage-4 리더 제거를 배제하고,
        // 팔로워 4명만 team0과 접촉시켜 rate-limit member 누적만 분리 검증한다.
        buffer.Add(6, 1, true, V(50.0f, 50.0f));
        buffer.Add(7, 1, false, V(0.10f, 0.20f));
        buffer.Add(8, 1, false, V(0.20f, 0.20f));
        buffer.Add(9, 1, false, V(0.00f, 0.30f));
        buffer.Add(10, 1, false, V(0.10f, 0.30f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = RateLimited();

        // 접촉 pair 24(=team0 6 x 근접 team1 팔로워 4). clamp01(24/8)=1.0, rate=10, dt=0.03 -> 0.3/호출. 3호출까지 누적 0.9(<1) => 전향 없음.
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
    /// [RateLimitConversion=true] 접촉 pair가 PairNormalizer 미만이면 clamp가 pairs/normalizer 비율로 스케일되어 누적 속도가 느려짐을 검증한다.
    /// </summary>
    [Test]
    public void RateLimited_ConversionRate_FractionalClamp_ConvertsOnSecondCall()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
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
        SimTuning tuning = RateLimited();

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
    /// [RateLimitConversion=true] 접촉이 끊긴 호출에서 해당 pair 누적값이 0으로 리셋되어, 재접촉 후 다시 처음부터 쌓임을 검증한다.
    /// </summary>
    [Test]
    public void RateLimited_Accumulator_ResetsWhenContactBreaks()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0: 5명.
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.10f, 0.00f));
        buffer.Add(2, 0, false, V(0.20f, 0.00f));
        buffer.Add(3, 0, false, V(0.00f, 0.10f));
        buffer.Add(4, 0, false, V(0.10f, 0.10f));
        // team1: 4명(리더 1 + 팔로워 3). 인덱스 5..8. 리더는 반경 밖(멀리) 두어 stage-4 리더 제거를 배제하고,
        // 팔로워 3명만 team0과 접촉시켜 누적/리셋만 분리 검증한다.
        buffer.Add(5, 1, true, V(50.0f, 50.0f));
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
        SimTuning tuning = RateLimited();

        // 접촉 pair 15(=team0 5 x 근접 team1 팔로워 3). clamp01(15/8)=1.0, rate=10, dt=0.03 -> 0.3/호출.
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
        using AgentBuffer buffer = new AgentBuffer(Cap);
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
        using AgentBuffer buffer = new AgentBuffer(Cap);
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
    /// [LeaderProtection=true] 국소 판정: 홀로 남은 리더는 CombatRadius 안 국소 수가 가장 많은 자격 적 팀에게 제거됨
    /// (거리 nearest가 아니라 국소 count 최대 규칙)을 검증한다. 두 적 팀은 리더 반대편에 배치해 서로 비접촉이라
    /// 3단계 상호 전향이 없다.
    /// </summary>
    [Test]
    public void Elimination_Attribution_MaxLocalCountWins()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0: 홀로 남은 리더(ownLocal=1).
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        // team1: 리더 근처 국소 2명(한쪽).
        buffer.Add(10, 1, true, V(0.5f, 0.5f));
        buffer.Add(11, 1, false, V(0.6f, 0.5f));
        // team2: 리더 근처 국소 3명(반대쪽) -> 국소 수 최대.
        buffer.Add(20, 2, true, V(-0.5f, -0.5f));
        buffer.Add(21, 2, false, V(-0.6f, -0.5f));
        buffer.Add(22, 2, false, V(-0.5f, -0.6f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        Assert.AreEqual(1, outcome.Eliminations.Count);
        CrowdElimination elim = outcome.Eliminations[0];
        Assert.AreEqual(0, elim.Team, "제거된 팀은 team0");
        Assert.AreEqual(2, elim.ByTeam, "국소 수 최대(3)인 team2가 제거");
        Assert.AreEqual(0, buffer.Id[elim.LeaderAgentIndex]);
        // 제거된 리더는 killer 팀으로의 전향도 함께 방출된다.
        Assert.IsTrue(ContainsConversion(buffer, outcome, 0, 2));
    }

    /// <summary>
    /// [LeaderProtection=true] 제거 후보 두 팀의 국소 수가 동률(둘 다 ownLocal 초과)이면 낮은 팀 id가 killer가 됨
    /// (count 동률 -> lower team)을 검증한다. 두 적 팀은 리더 반대편에 배치해 서로 비접촉 + 시작 인원 동수라
    /// 3단계 상호 전향이 없다.
    /// </summary>
    [Test]
    public void Elimination_Attribution_TieFavorsLowerTeam()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0: 홀로 남은 리더(ownLocal=1).
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        // team1: 리더 근처 국소 2명(한쪽).
        buffer.Add(10, 1, true, V(0.5f, 0.5f));
        buffer.Add(11, 1, false, V(0.6f, 0.5f));
        // team2: 리더 근처 국소 2명(반대쪽, 동률).
        buffer.Add(20, 2, true, V(-0.5f, -0.5f));
        buffer.Add(21, 2, false, V(-0.6f, -0.5f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        Assert.AreEqual(1, outcome.Eliminations.Count);
        Assert.AreEqual(0, outcome.Eliminations[0].Team);
        Assert.AreEqual(1, outcome.Eliminations[0].ByTeam, "국소 수 동률이면 낮은 팀 id(team1)가 제거");
    }

    /// <summary>
    /// [LeaderProtection=true] 국소 접촉한 홀로 남은 리더 둘(각 ownLocal=1, enemyLocal=1, 1 대 1)은 rule B의 국소
    /// 임계값(ownLocal<=1 && enemyLocal>=1)을 양쪽 다 충족하지만, 전역 시작 count가 동수(각 1)라 strict > 전역 가드가
    /// 양쪽 제거자 자격을 막아 서로 제거되지 않음을 검증한다(inert의 원인은 국소 비교가 아니라 전역 동수 가드).
    /// </summary>
    [Test]
    public void BothLoneLeaders_Inert()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
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
    /// [LeaderProtection=true] 홀로 남은 리더는 전역 시작 count가 strictly 더 큰 적 팀이 국소로 존재할 때만(rule B:
    /// ownLocal<=1 && enemyLocal>=1) 제거되고(a), 전역 count가 동수(1 대 1)인 이웃에게는 strict > 전역 가드가 자격을
    /// 막아 제거되지 않음을(b) 검증한다.
    /// </summary>
    [Test]
    public void LoneLeader_EliminatedOnlyVsStrictlyLarger()
    {
        // (a) team1이 리더 반경 안 국소 2명(> 1)이면 team0 리더가 제거된다.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));
            buffer.Add(1, 1, true, V(0.5f, 0.0f));
            buffer.Add(2, 1, false, V(0.4f, 0.3f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);
            resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

            Assert.AreEqual(1, outcome.Eliminations.Count, "국소 2명 이웃은 제거 가능");
            Assert.AreEqual(0, outcome.Eliminations[0].Team);
            Assert.AreEqual(1, outcome.Eliminations[0].ByTeam);
        }

        // (b) team1도 홀로(전역 count 1)면 team0과 전역 동수라 strict > 전역 가드가 막아 제거되지 않는다(국소는 rule B 임계값 충족).
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));
            buffer.Add(1, 1, true, V(0.5f, 0.0f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);
            resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

            Assert.AreEqual(0, outcome.Eliminations.Count, "전역 동수면 strict > 전역 가드가 막아 제거 불가");
        }
    }

    /// <summary>
    /// [LeaderProtection=true] 회귀 테스트: 코너에 몰린 리더는 map-separated straggler(반경 밖 같은 팀 팔로워)로
    /// 전역 count가 3이어도(구 전역 게이트라면 count!=1이라 제거 판정에서 제외되었을 것) CombatRadius 안에서 국소
    /// 수적 열세(ownLocal=1 < enemyLocal)면 제거+전향된다. straggler는 반경 밖이라 전향 대상이 아님을 함께 확인한다.
    /// </summary>
    [Test]
    public void Leader_LocallyOutnumbered_EliminatedDespiteDistantStragglers()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0(코너에 몰린 팀): 리더 + 반경 밖 멀리 떨어진 straggler 2명 -> 전역 count 3.
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        buffer.Add(1, 0, false, V(50.0f, 0.0f));
        buffer.Add(2, 0, false, V(50.0f, 1.0f));
        // team1(포위): 리더 반경 안 국소 4명 -> 전역 count 4(team0보다 커서 team0이 3단계 aggressor가 되지 않음).
        buffer.Add(10, 1, true, V(0.5f, 0.0f));
        buffer.Add(11, 1, false, V(0.4f, 0.3f));
        buffer.Add(12, 1, false, V(0.3f, -0.3f));
        buffer.Add(13, 1, false, V(0.6f, 0.3f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        Assert.AreEqual(1, outcome.Eliminations.Count, "전역 count 3이어도 국소 열세면 리더가 제거되어야 한다");
        Assert.AreEqual(0, outcome.Eliminations[0].Team, "제거된 팀은 team0");
        Assert.AreEqual(1, outcome.Eliminations[0].ByTeam, "국소 우세 team1이 제거");
        Assert.AreEqual(0, buffer.Id[outcome.Eliminations[0].LeaderAgentIndex], "제거된 것은 team0 리더 id0");
        // 방출 전향은 리더 흡수 1건뿐: 반경 밖 straggler는 전향되지 않는다(3단계 무전향, 4단계는 리더만).
        Assert.AreEqual(1, outcome.Conversions.Count, "반경 밖 straggler는 전향 대상이 아니다");
        Assert.IsTrue(ContainsConversion(buffer, outcome, 0, 1), "리더는 team1로 흡수 전향");
    }

    /// <summary>
    /// [rule B] 호위 없이 홀로 노출된(ownLocal=1) lone leader는 근처에 적이 있고(enemyLocal>=1) 그 적 팀이 전역적으로
    /// 더 크면 LeaderProtection ON/OFF 모두에서 제거된다. rule B에서 lone leader는 두 모드가 차이가 없고(ON/OFF 차이는
    /// 호위가 붙은 leader에서만 갈린다 -> <see cref="Leader_ImmuneWhileEscorted_EliminatedWhenExposed"/> 참고).
    /// team1 리더는 반경 안 팔로워가 있어 스스로는 홀로가 아니게 배치해 제거가 team0 리더 하나로 격리된다.
    /// </summary>
    [Test]
    public void LoneExposedLeader_EliminatedInBothModes()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0: 홀로 남은 리더(ownLocal=1).
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        // team1: 리더는 team0 리더 반경 안(거리 0.9) 국소 1명만 보이게, 팔로워는 team0 반경 밖·team1 리더 반경 안(거리 0.6).
        buffer.Add(10, 1, true, V(0.9f, 0.0f));
        buffer.Add(11, 1, false, V(1.5f, 0.0f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatOutcome outcome = new CombatOutcome(Cap);

        // ON(rule B): team0 리더는 국소적으로 홀로(ownLocal=1, 호위 0)이고 적이 하나 있으며(enemyLocal[1]=1)
        // team1 전역(2) > team0 전역(1)이라 -> 노출된 lone leader로 제거+전향된다.
        CombatState stateOn = new CombatState(TeamCount);
        resolver.Resolve(buffer, grid, Default(), 0.1f, stateOn, outcome);
        Assert.AreEqual(1, outcome.Eliminations.Count, "ON(rule B): 호위 없이 홀로 노출된 lone leader는 제거된다");
        Assert.AreEqual(0, outcome.Eliminations[0].Team, "제거된 팀은 team0");
        Assert.AreEqual(1, outcome.Eliminations[0].ByTeam, "제거자는 team1");
        Assert.AreEqual(0, buffer.Id[outcome.Eliminations[0].LeaderAgentIndex]);
        Assert.IsTrue(ContainsConversion(buffer, outcome, 0, 1), "리더는 team1로 흡수 전향");

        // OFF: 같은 배치에서 국소 동수(>=)면 team0 리더가 제거+전향된다(rule B에서 lone leader는 ON과 동일한 결과).
        CombatState stateOff = new CombatState(TeamCount);
        resolver.Resolve(buffer, grid, LeaderProtectionOff(), 0.1f, stateOff, outcome);
        Assert.AreEqual(1, outcome.Eliminations.Count, "OFF: 국소 동수(>=)면 제거된다");
        Assert.AreEqual(0, outcome.Eliminations[0].Team, "제거된 팀은 team0");
        Assert.AreEqual(1, outcome.Eliminations[0].ByTeam, "제거자는 team1");
        Assert.AreEqual(0, buffer.Id[outcome.Eliminations[0].LeaderAgentIndex]);
        Assert.IsTrue(ContainsConversion(buffer, outcome, 0, 1), "리더는 team1로 흡수 전향");
    }

    /// <summary>
    /// [rule B] LeaderProtection ON에서 leader는 CombatRadius 안에 아군 호위가 하나라도 있으면(ownLocal>=2) 국소적으로
    /// 수적 열세여도(enemyLocal=3 > ownLocal=2) 면역이다. 이 배치가 rule B를 유일하게 특정한다: 구 규칙("국소 열세/동수
    /// enemyLocal>=ownLocal면 제거")이라면 3>2로 제거되었을 것이므로, 면역은 오직 rule B(면역은 국소 열세 여부가 아니라
    /// 호위 유무로 결정 → ownLocal<=1이 아니면 제거 불가)로만 설명된다. 호위(id1)는 적 클러스터(+x) 반대편(-x)에 두어
    /// 리더 CombatRadius 안(거리 0.9)이면서 모든 적 member의 접촉 반경 밖(최근접 1.4 > 1.0)이라 3단계에서 전향되지 않고
    /// 4단계 ownLocal에 남는다. 호위가 사라져 홀로 노출되면(ownLocal=1) 같은 상대에게 제거된다.
    /// </summary>
    [Test]
    public void Leader_ImmuneWhileEscorted_EliminatedWhenExposed()
    {
        // (i) 호위 있음 + 국소 열세: team0 리더(id0, 원점) + 반대편 호위(id1, 거리 0.9) => ownLocal=2.
        //     team1(전역 3 > team0 전역 2)이 리더를 국소로 3명 포위(enemyLocal=3, 모두 반경 안) => 국소 3 대 2 열세.
        //     호위는 적 클러스터(+x) 반대편(-x)이라 가장 가까운 적과도 거리 1.4 > 접촉 반경 1.0 => 3단계 전향 없음 =>
        //     ownLocal=2 유지 => 리더는 국소 열세여도 면역(rule B: ownLocal<=1이 아니라 제거 불가). 구 규칙이면 3>2로 제거됐을 것.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));
            buffer.Add(1, 0, false, V(-0.9f, 0.0f));
            buffer.Add(10, 1, true, V(0.5f, 0.0f));
            buffer.Add(11, 1, false, V(0.5f, 0.3f));
            buffer.Add(12, 1, false, V(0.5f, -0.3f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);

            resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

            // 호위가 3단계에서 전향되지 않아야 면역 판정이 rule B로만 설명된다(호위가 벗겨지면 4단계에서 홀로 판정됨).
            Assert.IsFalse(ContainsConversion(buffer, outcome, 1, 1), "호위(id1)는 적 접촉 반경 밖이라 3단계에서 전향되지 않는다");
            Assert.AreEqual(0, outcome.Conversions.Count, "호위 생존 + 리더 흡수 없음 => 전향 0건");
            Assert.AreEqual(0, outcome.Eliminations.Count, "호위가 붙은 leader는 국소 3 대 2 열세여도 면역이다(rule B: ownLocal<=1 아님)");
        }

        // (ii) 같은 배치에서 호위(id1)만 제거: team0 리더가 홀로 노출(ownLocal=1) + 국소 적 3(enemyLocal=3)
        //      + 전역 가드(team1 3 > team0 1) => 같은 team1에게 제거+흡수된다.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));
            buffer.Add(10, 1, true, V(0.5f, 0.0f));
            buffer.Add(11, 1, false, V(0.5f, 0.3f));
            buffer.Add(12, 1, false, V(0.5f, -0.3f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);

            resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

            Assert.AreEqual(1, outcome.Eliminations.Count, "호위가 사라져 홀로 노출된 leader는 같은 상대에게 제거된다");
            Assert.AreEqual(0, outcome.Eliminations[0].Team, "제거된 팀은 team0");
            Assert.AreEqual(1, outcome.Eliminations[0].ByTeam, "제거자는 team1");
            Assert.AreEqual(0, buffer.Id[outcome.Eliminations[0].LeaderAgentIndex]);
            Assert.IsTrue(ContainsConversion(buffer, outcome, 0, 1), "리더는 team1로 흡수 전향");
        }
    }

    /// <summary>
    /// [회귀·핵심 버그] 전역적으로 더 크지만 국소 호위가 얇은 crowd의 leader가, 전역적으로 더 작지만 국소로 리더를
    /// 둘러싼 crowd에게 제거되지 않음을 검증한다. RateLimitConversion=true 첫 호출은 예산이 아직 0이라 3단계 전향이
    /// 일어나지 않으므로(즉시 전향 모드였다면 큰 팀이 국소 적을 먼저 흡수해 버그가 드러나지 않는다) 국소 밀집한 적이
    /// 4단계까지 살아남아 국소 열세를 만든다. 전역 가드가 없다면 이 leader가 제거되고, 가드가 있으면 제거되지 않는다
    /// (가드 적용 전에는 실패, 적용 후 통과).
    /// </summary>
    [Test]
    public void Leader_NotEliminated_ByGloballySmallerButLocallyDenserEnemy()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0(전역 큼, 6명): 리더는 원점, 팔로워 5명은 CombatRadius(1.0) 밖 멀리 => 리더의 국소 호위 = 0.
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        buffer.Add(1, 0, false, V(10.0f, 0.0f));
        buffer.Add(2, 0, false, V(10.0f, 1.0f));
        buffer.Add(3, 0, false, V(10.0f, 2.0f));
        buffer.Add(4, 0, false, V(10.0f, 3.0f));
        buffer.Add(5, 0, false, V(10.0f, 4.0f));
        // team1(전역 작음, 3명): team0 리더 국소 반경 안에 밀집 => 국소 3 대 1로 우세.
        buffer.Add(10, 1, true, V(0.3f, 0.0f));
        buffer.Add(11, 1, false, V(0.3f, 0.3f));
        buffer.Add(12, 1, false, V(0.0f, 0.3f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        // clamp01(3/8)=0.375, rate=10, dt=0.1 -> 누적 0.375(<1) => 첫 호출 3단계 전향 없음 => 국소 밀집 적이 4단계까지 유지.
        resolver.Resolve(buffer, grid, RateLimited(), 0.1f, state, outcome);

        // 전역 가드: team1(전역 3) <= team0(전역 6)이라 국소 우세(3>1)에도 team0 리더는 제거되지 않는다.
        Assert.AreEqual(0, outcome.Eliminations.Count, "전역적으로 더 큰 crowd의 리더가 더 작은(국소 밀집) crowd에게 먹히면 안 된다");
        Assert.AreEqual(0, outcome.Conversions.Count, "첫 호출 예산 0이라 3단계 전향도, 4단계 리더 흡수도 없어야 한다");
    }

    /// <summary>
    /// [확인] 전역적으로 더 큰 crowd는 접촉한 더 작은 적의 leader를 국소 접촉만으로 제거한다. 이때 작은 적이 반경 밖
    /// straggler를 둬 전역 count가 1이 아니라 3이어도(과거 "count==1 lone leader만 제거" 규칙이라면 안전했을 것)
    /// 전역 크기가 결정하므로 제거된다. 전역 가드는 killer가 전역적으로 더 크기만 하면 정상 제거를 막지 않는다.
    /// </summary>
    [Test]
    public void Leader_Eliminated_ByGloballyLargerEnemy_DespiteEnemyStragglers()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team1(피해자, 전역 3): 리더는 원점, straggler 2명은 반경 밖 멀리 => 전역 count 3(>1)이지만 국소는 리더 홀로.
        buffer.Add(10, 1, true, V(0.0f, 0.0f));
        buffer.Add(11, 1, false, V(50.0f, 0.0f));
        buffer.Add(12, 1, false, V(50.0f, 1.0f));
        // team0(killer, 전역 4): 리더 + 팔로워 3명이 team1 리더를 국소 포위.
        buffer.Add(0, 0, true, V(0.5f, 0.0f));
        buffer.Add(1, 0, false, V(0.3f, 0.3f));
        buffer.Add(2, 0, false, V(0.3f, -0.3f));
        buffer.Add(3, 0, false, V(0.6f, 0.3f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        // team0(전역 4) > team1(전역 3)이라 가드 통과 + 국소 4>1 => team1 리더 제거.
        Assert.AreEqual(1, outcome.Eliminations.Count, "전역적으로 더 큰 crowd는 straggler로 count가 3이어도 작은 적 리더를 제거한다");
        Assert.AreEqual(1, outcome.Eliminations[0].Team, "제거된 팀은 team1");
        Assert.AreEqual(0, outcome.Eliminations[0].ByTeam, "제거자는 team0");
        Assert.AreEqual(10, buffer.Id[outcome.Eliminations[0].LeaderAgentIndex], "제거된 것은 team1 리더 id10");
        // 반경 밖 straggler는 전향되지 않고, 방출 전향은 리더 흡수 1건뿐(전역 크기가 결정, count==1이 아님).
        Assert.AreEqual(1, outcome.Conversions.Count, "반경 밖 straggler는 전향 대상이 아니다");
        Assert.IsTrue(ContainsConversion(buffer, outcome, 10, 0), "리더는 team0로 흡수 전향");
    }

    /// <summary>
    /// [회귀] 전역 동수(strict > 가드)면 국소 우세가 있어도 leader가 제거되지 않고, 이는 LeaderProtection ON/OFF 두
    /// 모드 모두에 적용됨을 검증한다(가드가 토글 바깥). 전역 동수라 3단계 전향도 없어(strict >) 국소 밀집 적이 4단계까지
    /// 그대로 남지만, 전역 가드가 양쪽에서 제거를 막는다. 가드 도입 전에는 두 모드 모두에서 team0 리더가 제거되었다.
    /// </summary>
    [Test]
    public void EqualGlobalSize_NoLeaderElimination_InBothModes()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0(전역 3): 리더는 원점, 팔로워 2명은 반경 밖 멀리 => 리더 국소 호위 0.
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        buffer.Add(1, 0, false, V(50.0f, 0.0f));
        buffer.Add(2, 0, false, V(50.0f, 1.0f));
        // team1(전역 3, 동수): team0 리더 국소 반경 안 밀집 3명 => 국소 3 대 1.
        buffer.Add(10, 1, true, V(0.3f, 0.0f));
        buffer.Add(11, 1, false, V(0.3f, 0.3f));
        buffer.Add(12, 1, false, V(0.0f, 0.3f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatOutcome outcome = new CombatOutcome(Cap);

        // ON: 전역 동수(3==3)라 국소 우세(3>1)에도 team0 리더 제거 없음.
        CombatState stateOn = new CombatState(TeamCount);
        resolver.Resolve(buffer, grid, Default(), 0.1f, stateOn, outcome);
        Assert.AreEqual(0, outcome.Eliminations.Count, "ON: 전역 동수면 국소 우세여도 제거되지 않는다");
        Assert.AreEqual(0, outcome.Conversions.Count, "ON: 전역 동수면 전향/흡수 없음");

        // OFF: 같은 배치에서 전역 동수면 국소 동률(>=) 규칙이어도 가드가 우선해 제거 없음(가드는 토글 바깥).
        CombatState stateOff = new CombatState(TeamCount);
        resolver.Resolve(buffer, grid, LeaderProtectionOff(), 0.1f, stateOff, outcome);
        Assert.AreEqual(0, outcome.Eliminations.Count, "OFF: 전역 동수면 가드가 토글보다 우선해 제거되지 않는다");
        Assert.AreEqual(0, outcome.Conversions.Count, "OFF: 전역 동수면 전향/흡수 없음");
    }

    /// <summary>
    /// [RateLimitConversion=true] 여러 승자 팀이 같은 victim을 노리면 가장 가까운 팀이 가져가고, 밀린 팀의 예산은 소모되지 않고 다음 호출로 이월됨을 검증한다.
    /// </summary>
    [Test]
    public void RateLimited_VictimClaimableByTwoTeams_NearestWins_OtherBudgetRetained()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
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
        SimTuning tuning = RateLimited();

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
        using AgentBuffer buffer = new AgentBuffer(Cap);
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

    /// <summary>
    /// [RateLimitConversion=false] 큰 팀과 접촉한 작은 팀의 non-leader member 전원이 단 한 번의 Resolve 호출에서 모두 전향함을 검증한다(접촉 즉시 전향).
    /// 작은 팀 리더는 반경 밖에 두어 이 호출에서 전향/제거되지 않게 해 팔로워 즉시 전향만 분리 검증한다.
    /// </summary>
    [Test]
    public void Instant_AllContactingFollowers_ConvertInSingleCall()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0(승자): 6명(리더 1 + 팔로워 5), 원점 부근 밀집.
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.10f, 0.00f));
        buffer.Add(2, 0, false, V(0.20f, 0.00f));
        buffer.Add(3, 0, false, V(0.00f, 0.10f));
        buffer.Add(4, 0, false, V(0.10f, 0.10f));
        buffer.Add(5, 0, false, V(0.20f, 0.10f));
        // team1(패자, 4명): 리더는 반경 밖(멀리), 팔로워 3명은 team0과 접촉.
        buffer.Add(6, 1, true, V(50.0f, 50.0f));
        buffer.Add(7, 1, false, V(0.30f, 0.00f));
        buffer.Add(8, 1, false, V(0.30f, 0.10f));
        buffer.Add(9, 1, false, V(0.10f, 0.20f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        // 단 한 번의 호출로 접촉한 팔로워 3명이 모두 team0으로 전향한다.
        resolver.Resolve(buffer, grid, Default(), 0.03f, state, outcome);

        Assert.AreEqual(3, outcome.Conversions.Count, "접촉한 team1 팔로워 전원이 한 호출에 전향해야 한다");
        Assert.AreEqual(0, outcome.Eliminations.Count, "team1 리더는 반경 밖이라 제거되지 않는다");
        for (int i = 0; i < outcome.Conversions.Count; i++)
        {
            CrowdConversion c = outcome.Conversions[i];
            Assert.AreEqual(1, buffer.Team[c.AgentIndex], "victim은 team1 소속이어야 한다");
            Assert.IsFalse(buffer.IsLeader[c.AgentIndex], "리더는 전향 대상이 아니다");
            Assert.AreEqual(0, c.ToTeam, "전향 대상 팀은 team0");
        }
    }

    /// <summary>
    /// [RateLimitConversion=false] 작은 팀(3명)이 훨씬 큰 팀(10명)에게 반경 안에서 완전히 포위되면, 팔로워 전원 즉시 전향 후
    /// 홀로 남은 리더도 같은 호출의 제거 pass에서 흡수되어 팀 전체가 한 번에 사라짐을 검증한다.
    /// (제거 pass가 같은 호출의 전향 결과를 반영하는 것은 기존 동작이며, 즉시 전향으로 첫 호출에 lone-leader 조건이 성립한다.)
    /// </summary>
    [Test]
    public void Instant_SmallTeamSurrounded_AllMembersFlipInSingleCall()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0(작은 팀, 3명): 리더 + 팔로워 2, 중심에 밀집.
        buffer.Add(100, 0, true, V(0.00f, 0.00f));
        buffer.Add(101, 0, false, V(0.10f, 0.00f));
        buffer.Add(102, 0, false, V(0.00f, 0.10f));
        // team1(큰 팀, 10명): 리더 1 + 팔로워 9, 작은 팀을 반경 안에서 포위.
        buffer.Add(0, 1, true, V(0.40f, 0.40f));
        buffer.Add(1, 1, false, V(0.30f, 0.00f));
        buffer.Add(2, 1, false, V(0.30f, 0.10f));
        buffer.Add(3, 1, false, V(0.30f, 0.20f));
        buffer.Add(4, 1, false, V(0.00f, 0.30f));
        buffer.Add(5, 1, false, V(0.10f, 0.30f));
        buffer.Add(6, 1, false, V(0.20f, 0.30f));
        buffer.Add(7, 1, false, V(-0.10f, 0.00f));
        buffer.Add(8, 1, false, V(-0.10f, 0.10f));
        buffer.Add(9, 1, false, V(-0.20f, 0.00f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.03f, state, outcome);

        // 팔로워 2명 즉시 전향 + 홀로 남은 리더 제거 흡수 전향 1건 = 전향 3건.
        Assert.AreEqual(3, outcome.Conversions.Count, "작은 팀 3명 전원이 team1로 전향");
        Assert.AreEqual(1, outcome.Eliminations.Count, "포위된 lone leader는 같은 호출에 제거된다");
        Assert.AreEqual(0, outcome.Eliminations[0].Team, "제거된 팀은 team0");
        Assert.AreEqual(1, outcome.Eliminations[0].ByTeam, "제거자는 team1");
        Assert.AreEqual(100, buffer.Id[outcome.Eliminations[0].LeaderAgentIndex], "제거된 것은 team0 리더 id100");
        for (int i = 0; i < outcome.Conversions.Count; i++)
        {
            Assert.AreEqual(1, outcome.Conversions[i].ToTeam, "전향 대상은 team1");
        }
    }

    /// <summary>
    /// [RateLimitConversion=false] 여러 승자 팀이 같은 victim에 접촉하면 가장 가까운 팀이 배타적으로 victim을 즉시 가져감을 검증한다(배타 배정 + 즉시 전향).
    /// </summary>
    [Test]
    public void Instant_VictimClaimableByTwoTeams_NearestWins()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
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

        // 한 번의 호출로 v는 더 가까운 team0에만 배타 배정되어 즉시 전향한다.
        resolver.Resolve(buffer, grid, Default(), 0.06f, state, outcome);
        Assert.IsTrue(ContainsConversion(buffer, outcome, 100, 0), "더 가까운 team0가 v를 즉시 가져간다");
        Assert.IsFalse(ContainsConversion(buffer, outcome, 100, 1), "동시에 team1으로는 전향되지 않는다(배타 배정)");
    }

    /// <summary>
    /// [LeaderProtection=false] 전역 가드 추가 후: 전역 동수(각 count=1)로 접촉한 lone leader 둘은 OFF 모드(>=)여도
    /// 서로를 제거하지 못한다. 전역 가드가 strict >라 전역 동수는 어느 쪽도 제거자 자격을 얻지 못하고(가드가 ON/OFF
    /// 토글보다 우선), 따라서 상호/순환 제거 쌍이 방출되지 않는다. 전역 count가 전순서를 이루므로 제거는 acyclic이다
    /// (A가 B를 제거하려면 count[A]>count[B]라 A,B 상호 제거는 불가능). 가드 도입 전 이 배치는 순환 쌍을 방출했다.
    /// </summary>
    [Test]
    public void ModeOff_EqualGlobalLoneLeaders_NoCyclicElimination()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0, team1의 lone leader가 CombatRadius(1.0) 안(거리 0.5)에서 접촉. 각 전역 count=1로 동수.
        buffer.Add(0, 0, true, V(0.0f, 0.0f));
        buffer.Add(1, 1, true, V(0.5f, 0.0f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, LeaderProtectionOff(), 0.1f, state, outcome);

        // 전역 동수(strict > 가드)라 OFF(>=) 국소 동수여도 어느 쪽도 제거되지 않는다: 순환 제거 쌍이 생기지 않는다.
        Assert.AreEqual(0, outcome.Eliminations.Count, "전역 동수 lone leader 둘은 OFF 모드여도 서로 제거되지 않는다(전역 가드가 토글보다 우선)");
        Assert.AreEqual(0, outcome.Conversions.Count);
    }

    /// <summary>
    /// [LeaderProtection=true] 한 번의 Resolve가 비순환 체인 제거(team2가 team0에게, team0이 team1에게 제거되고
    /// team1은 생존)를 방출함을 검증한다. 즉 중간 팀 team0이 자기도 이번 tick에 제거되면서 동시에 team2의 killer로
    /// 기록된다. 이 형태가 CommitOutcomes의 BUG 2(제거된 중간 팀에 ex-리더/팔로워가 남는 zombie) 트리거다.
    /// 주의: 여기서는 resolver 방출 "기록"만 검증한다. 체인을 최종 live 생존 팀(team1)까지 해소하는 CommitOutcomes
    /// 로직은 CrowdRoot 소관이라 이 유닛 테스트 범위 밖이며 Unity Test Runner + 플레이테스트로 확인해야 한다.
    /// </summary>
    [Test]
    public void ChainElimination_MiddleTeamAlsoEliminated_EmitsChainRecords()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team1(생존, 국소 강): 리더 + 원점 팔로워 2 + 반경 밖 팔로워 1. 원점 밀집 3명이 team0 리더를 국소 열세로
        // 만들고, 전역 count 4 > team0 전역 count 3이라 전역 가드를 통과해 team0 리더의 killer가 될 수 있다.
        buffer.Add(10, 1, true, V(0.0f, 0.0f));
        buffer.Add(11, 1, false, V(0.2f, 0.0f));
        buffer.Add(12, 1, false, V(0.0f, 0.2f));
        buffer.Add(13, 1, false, V(100.0f, 100.0f));
        // team0(중간): 리더는 team1 국소 반경 안(거리 0.6)이라 team1에게 제거된다.
        buffer.Add(0, 0, true, V(0.6f, 0.0f));
        // team0의 팔로워 2명은 멀리 떨어진 team2 리더 옆에 두어, team2를 국소 열세로 만들되 team0 리더 판정에는 안 잡히게 한다.
        buffer.Add(1, 0, false, V(2.7f, 0.0f));
        buffer.Add(2, 0, false, V(3.0f, 0.3f));
        // team2(lone leader): team0 팔로워 2명에게 국소 포위(거리 0.3)되어 team0에게 제거된다. team1/team0 리더와는 비접촉.
        buffer.Add(20, 2, true, V(3.0f, 0.0f));

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        // 체인: team2 -> team0 -> team1(생존). 중간 team0이 자기도 제거되면서 team2의 killer로 기록된다.
        Assert.AreEqual(2, outcome.Eliminations.Count, "team2와 team0 둘이 제거되어야 한다(team1은 생존)");
        Assert.IsTrue(ContainsElimination(outcome, 2, 0), "team2가 team0에게 제거되는 기록(중간 팀이 killer)");
        Assert.IsTrue(ContainsElimination(outcome, 0, 1), "team0가 team1에게 제거되는 기록(중간 팀도 제거됨)");
        for (int i = 0; i < outcome.Eliminations.Count; i++)
        {
            Assert.AreNotEqual(1, outcome.Eliminations[i].Team, "team1은 생존해야 한다(제거 기록에 없음)");
        }
    }

    /// <summary>
    /// [스케일 인지] 스케일 1.5인 승자 팀 유닛은 base CombatRadius(1.0)를 넘는 거리(1.1 &lt; radius*1.25=1.25)의 적을
    /// pair 평균 반경으로 접촉해 전향시킨다. 같은 배치에서 스케일 1이면(대조 테스트) pair 반경이 radius로 줄어 접촉하지 못한다.
    /// </summary>
    [Test]
    public void SizeAware_LargeUnit_ConvertsEnemyBeyondBaseRadius()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0(승자, 3명 > team1 2명):
        buffer.Add(0, 0, true, V(40.0f, 40.0f));           // 리더: 멀리(stage-4 리더 제거 배제, 접촉 유닛 아님).
        buffer.Add(1, 0, false, V(1.1f, 0.0f), 1.5f);      // 큰 유닛(스케일 1.5): victim에 거리 1.1(radius 1.0 초과, radius*1.25=1.25 이내).
        buffer.Add(2, 0, false, V(41.0f, 40.0f), 1.5f);    // 멀리(count 채우기).
        // team1(패자, 2명):
        buffer.Add(10, 1, false, V(0.0f, 0.0f));           // victim(스케일 1): 원점.
        buffer.Add(11, 1, true, V(50.0f, 50.0f));          // 리더: 멀리.

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        // pairR = radius*0.5*(1.5+1.0)=1.25 > 1.1 => 접촉. team0(3)>team1(2) => victim 전향.
        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        Assert.AreEqual(0, outcome.Eliminations.Count, "멀리 떨어진 리더들은 제거되지 않는다");
        Assert.AreEqual(1, outcome.Conversions.Count, "큰 유닛(1.5)은 radius를 넘는 1.1 거리의 적을 접촉·전향시킨다");
        CrowdConversion c = outcome.Conversions[0];
        Assert.AreEqual(10, buffer.Id[c.AgentIndex], "victim은 team1 follower id10");
        Assert.AreEqual(0, c.ToTeam, "전향 대상 팀은 team0");
    }

    /// <summary>
    /// [스케일 인지 대조] 위 배치에서 접촉 유닛이 스케일 1이면 pair 반경이 base radius(1.0)로 줄어 거리 1.1 적을 접촉하지
    /// 못하고 전향이 일어나지 않는다(스케일 1 pair는 기존 base radius 경계를 그대로 쓴다).
    /// </summary>
    [Test]
    public void SizeAware_BaselineUnit_DoesNotConvertBeyondBaseRadius()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // team0(승자, 3명 > team1 2명):
        buffer.Add(0, 0, true, V(40.0f, 40.0f));           // 리더: 멀리.
        buffer.Add(1, 0, false, V(1.1f, 0.0f));            // 접촉 유닛(스케일 1, 기본): victim에 거리 1.1(base radius 1.0 초과).
        buffer.Add(2, 0, false, V(41.0f, 40.0f));          // 멀리(count 채우기).
        // team1(패자, 2명):
        buffer.Add(10, 1, false, V(0.0f, 0.0f));           // victim(스케일 1): 원점.
        buffer.Add(11, 1, true, V(50.0f, 50.0f));          // 리더: 멀리.

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);

        // pairR = radius*0.5*(1.0+1.0)=1.0 < 1.1 => 접촉 없음 => 전향 없음.
        resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

        Assert.AreEqual(0, outcome.Conversions.Count, "스케일 1 유닛은 base radius(1.0) 밖 1.1 거리 적을 접촉하지 못한다");
        Assert.AreEqual(0, outcome.Eliminations.Count);
    }

    /// <summary>
    /// [LeaderAloneRadius split] 아군 호위 판정 반경(LeaderAloneRadius)이 적 접촉 반경(CombatRadius)과 독립임을 증명한다.
    /// (i-a) CombatRadius(1.0) 밖·LeaderAloneRadius(2.0) 안 거리 1.5의 아군 호위는 넓힌 LeaderAloneRadius에서 ownLocal에
    /// 집계되어 리더가 홀로가 아니게 되므로 면역이다. (i-b) 같은 배치에서 LeaderAloneRadius==CombatRadius(1.0)면 그 호위는
    /// 집계되지 않아 리더가 홀로(ownLocal=1)로 판정되어 제거된다 -> split이 결과를 가른다. (ii) 같은 거리 1.5의 "적"은
    /// LeaderAloneRadius를 넓혀도 enemyLocal에 집계되지 않는다(적은 여전히 CombatRadius) -> LeaderAloneRadius 확대가 적
    /// 탐지 범위를 넓히지 않음을 확인한다.
    /// </summary>
    [Test]
    public void LeaderAloneRadius_Split_EscortRangeIndependentFromEnemyContactRange()
    {
        // (i-a) 넓힌 LeaderAloneRadius(2.0): 아군 호위(거리 1.5)가 ownLocal에 잡혀 리더 면역.
        //   호위(id1)는 적 클러스터(+x) 반대편(-x)에 두어 적 접촉 반경(CombatRadius) 밖 -> 3단계에서 전향되지 않고 ownLocal에 남는다.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));    // team0 리더(원점).
            buffer.Add(1, 0, false, V(-1.5f, 0.0f));  // 아군 호위: 거리 1.5 (CombatRadius 1.0 < 1.5 <= LeaderAloneRadius 2.0).
            // team1(전역 3 > team0 전역 2): 리더 CombatRadius 안 국소 3명(+x쪽).
            buffer.Add(10, 1, true, V(0.5f, 0.0f));
            buffer.Add(11, 1, false, V(0.5f, 0.3f));
            buffer.Add(12, 1, false, V(0.5f, -0.3f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);

            resolver.Resolve(buffer, grid, WideLeaderAlone(), 0.1f, state, outcome);

            Assert.IsFalse(ContainsConversion(buffer, outcome, 1, 1), "호위(id1)는 적 접촉 반경 밖이라 3단계에서 전향되지 않는다");
            Assert.AreEqual(0, outcome.Eliminations.Count,
                "거리 1.5 아군 호위가 넓힌 LeaderAloneRadius(2.0) 안에 잡혀 ownLocal=2 -> 리더 면역");
        }

        // (i-b) 동일 배치에서 LeaderAloneRadius==CombatRadius(1.0): 거리 1.5 호위는 집계되지 않아 리더 홀로(ownLocal=1) -> 제거.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));
            buffer.Add(1, 0, false, V(-1.5f, 0.0f));
            buffer.Add(10, 1, true, V(0.5f, 0.0f));
            buffer.Add(11, 1, false, V(0.5f, 0.3f));
            buffer.Add(12, 1, false, V(0.5f, -0.3f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);

            resolver.Resolve(buffer, grid, Default(), 0.1f, state, outcome);

            Assert.AreEqual(1, outcome.Eliminations.Count,
                "LeaderAloneRadius==CombatRadius면 거리 1.5 호위가 집계되지 않아 리더가 홀로로 제거된다(split이 결과를 가른다)");
            Assert.AreEqual(0, outcome.Eliminations[0].Team, "제거된 팀은 team0");
            Assert.AreEqual(1, outcome.Eliminations[0].ByTeam, "제거자는 team1");
        }

        // (ii) 넓힌 LeaderAloneRadius(2.0)여도 거리 1.5의 "적"은 enemyLocal에 집계되지 않는다(적은 여전히 CombatRadius).
        //   리더는 홀로(ownLocal=1)이고 전역 가드도 통과(team1 3 > team0 1)하지만 유일한 적이 CombatRadius 밖이라 제거되지 않는다.
        //   적 탐지가 LeaderAloneRadius로 잘못 넓혀졌다면 이 리더는 제거되었을 것이다.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            buffer.Add(0, 0, true, V(0.0f, 0.0f));     // team0 리더(홀로, 호위 없음).
            buffer.Add(10, 1, true, V(1.5f, 0.0f));    // 적(거리 1.5): CombatRadius 1.0 밖, LeaderAloneRadius 2.0 안.
            buffer.Add(11, 1, false, V(50.0f, 50.0f)); // team1 전역 count 채우기(원거리, 국소 무관).
            buffer.Add(12, 1, false, V(51.0f, 50.0f));

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);

            resolver.Resolve(buffer, grid, WideLeaderAlone(), 0.1f, state, outcome);

            Assert.AreEqual(0, outcome.Eliminations.Count,
                "거리 1.5 적은 LeaderAloneRadius를 넓혀도 enemyLocal에 집계되지 않는다(적은 CombatRadius 유지) -> 리더 제거 안 됨");
        }
    }

    /// <summary>
    /// [UseDynamicConvertRate=true] 전향율이 이긴 팀의 <b>틱 시작 인원수</b>에 비례함을 검증한다. 접촉 배치를 그대로 두고
    /// 접촉 반경 밖에 승자 팀원만 더해 시작 count를 2배로 만들면(접촉 pair 수와 포화된 clamp는 그대로) 전향까지 걸리는
    /// 호출 수가 절반이 된다. 즉 달라진 변수는 전향율 하나뿐이다.
    /// </summary>
    [Test]
    public void DynamicRate_ConversionRate_ScalesWithWinnerStartCount()
    {
        // (a) 승자 team0 시작 count 6 => rate = 6 * 0.25 = 1.5/s. dt=0.2 -> 0.3/호출. 3호출 0.9(<1) => 전향 없음, 4호출 1.2 => 1건.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            AddDynamicRateContactLayout(buffer);

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);
            SimTuning tuning = DynamicRate(0.25f);

            for (int call = 1; call <= 3; call++)
            {
                resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
                Assert.AreEqual(0, outcome.Conversions.Count, "count 6, call " + call + ": 누적 " + (0.3f * call) + " < 1");
            }

            resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
            Assert.AreEqual(1, outcome.Conversions.Count, "count 6이면 4호출째(누적 1.2)에 전향 1건");
            Assert.AreEqual(0, outcome.Eliminations.Count, "team1 리더는 반경 밖이라 제거되지 않는다");
            Assert.AreEqual(0, outcome.Conversions[0].ToTeam, "전향 대상 팀은 team0");
        }

        // (b) 같은 접촉 배치 + 접촉 반경 밖 team0 팔로워 6명 => 시작 count만 12가 된다. 접촉 pair 수(24)도 포화 clamp(1.0)도
        //     그대로이므로 바뀌는 것은 전향율뿐: rate = 12 * 0.25 = 3.0/s -> 0.6/호출 -> 2호출째에 전향.
        {
            using AgentBuffer buffer = new AgentBuffer(Cap);
            AddDynamicRateContactLayout(buffer);
            for (int i = 0; i < 6; i++)
            {
                buffer.Add(20 + i, 0, false, V(60.0f + i, 60.0f));
            }

            SpatialGrid grid = NewGrid(buffer);
            CombatResolver resolver = new CombatResolver(TeamCount, Cap);
            CombatState state = new CombatState(TeamCount);
            CombatOutcome outcome = new CombatOutcome(Cap);
            SimTuning tuning = DynamicRate(0.25f);

            resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
            Assert.AreEqual(0, outcome.Conversions.Count, "count 12여도 1호출 누적 0.6 < 1이면 전향 없음");

            resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
            Assert.AreEqual(1, outcome.Conversions.Count, "시작 count가 2배면 절반의 호출(2호출째)에 전향한다");
            Assert.AreEqual(0, outcome.Eliminations.Count);
            Assert.AreEqual(0, outcome.Conversions[0].ToTeam, "전향 대상 팀은 team0");
        }
    }

    /// <summary>
    /// [UseDynamicConvertRate=true] 전향율이 <b>호출마다 다시</b> 계산되고, 그때 이미 쌓여 있던 소수 누적값과 정상적으로
    /// 합쳐짐을 검증한다. count 6로 0.6을 쌓아 둔 뒤 count를 12로 올리면 그 다음 한 호출(+0.6)만으로 1.2가 되어 전향한다 —
    /// 전향율이 갱신되지 않았다면 0.9(<1)로 전향이 없고, 누적값이 유실됐다면 0.6(<1)으로 역시 전향이 없다.
    /// </summary>
    [Test]
    public void DynamicRate_RateRecomputedPerCall_ComposesWithExistingCarry()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        AddDynamicRateContactLayout(buffer);
        // 접촉 반경 밖 team3 6명. 나중에 team0으로 옮겨 승자의 시작 count만 6 -> 12로 바꾸는 데 쓴다.
        // 원거리라 접촉 pair가 없고 리더도 없어, 옮기기 전까지 어느 판정에도 관여하지 않는다.
        for (int i = 0; i < 6; i++)
        {
            buffer.Add(20 + i, 3, false, V(60.0f + i, 60.0f));
        }

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = DynamicRate(0.25f);

        // 1단계: count 6 -> rate 1.5 -> 0.3/호출. 2호출로 0.6을 쌓는다(아직 전향 없음).
        for (int call = 1; call <= 2; call++)
        {
            resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
            Assert.AreEqual(0, outcome.Conversions.Count, "carry 적립 call " + call);
        }

        // 2단계: 원거리 team3 6명을 team0으로 옮겨 시작 count를 12로 만든다(팀 변경이므로 grid 재구성 필요).
        for (int i = 0; i < buffer.Count; i++)
        {
            if (buffer.Team[i] == 3)
            {
                buffer.Team[i] = 0;
            }
        }
        grid.Rebuild(buffer);

        // rate가 3.0으로 갱신되어 0.6 + 0.6 = 1.2 >= 1 => 전향 1건.
        resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
        Assert.AreEqual(1, outcome.Conversions.Count,
            "갱신된 전향율(0.6)이 기존 누적(0.6)에 더해져 1.2가 되어야 한다 — 갱신 안 됐다면 0.9, 누적 유실이면 0.6으로 전향 없음");
        Assert.AreEqual(0, outcome.Conversions[0].ToTeam, "전향 대상 팀은 team0");

        // 방출분(1)만 소모되고 남은 0.2는 그대로 이월된다: 0.2 + 0.6 = 0.8(<1) => 전향 없음, 그다음 1.4 => 전향 1건.
        resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
        Assert.AreEqual(0, outcome.Conversions.Count, "남은 0.2 + 0.6 = 0.8 < 1이면 전향 없음");
        resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
        Assert.AreEqual(1, outcome.Conversions.Count, "0.8 + 0.6 = 1.4 >= 1이면 전향 1건");
    }

    /// <summary>
    /// [UseDynamicConvertRate=true] 계수가 0이면 승자 인원수가 아무리 많아도 전향율이 0이라 아무도 전향하지 않음을 검증한다.
    /// (이 성질은 RateLimitConversion=ON에서만 성립한다 — OFF면 예산 게이트 자체가 없어 접촉 즉시 전향한다. 이 fixture는 ON이다.)
    /// </summary>
    [Test]
    public void DynamicRate_ZeroPerMember_ConvertsNobody()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        AddDynamicRateContactLayout(buffer);

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        SimTuning tuning = DynamicRate(0f);

        for (int call = 1; call <= 20; call++)
        {
            resolver.Resolve(buffer, grid, tuning, 0.2f, state, outcome);
            Assert.AreEqual(0, outcome.Conversions.Count, "계수 0이면 누적이 0에 머물러 전향이 없다 (call " + call + ")");
            Assert.AreEqual(0, outcome.Eliminations.Count, "call " + call);
        }
    }

    /// <summary>
    /// [UseDynamicConvertRate=true] 동적 전향율도 <b>agent 삽입 순서의 순열에 불변</b>임을 검증한다. 전향율이 틱 시작
    /// 스냅샷(_startCounts)에서만 나오므로 삽입 순서가 바뀌어도 같은 값이 되어야 한다.
    /// 예산이 정확히 1이 되도록 계수를 잡아, 가장 가까운 victim 1명만 전향한다 — 고정 ConvertPerSecond 분기를 탔다면
    /// 오염값(100000) 때문에 예산이 폭발해 전향/제거가 쏟아지므로 아래 서명 비교에서 즉시 드러난다.
    /// </summary>
    [Test]
    public void DynamicRate_ThreeWayContact_PermutationInvariant()
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

        // team0 시작 count 4 * 계수 1.0 = rate 4.0, 접촉 pair 12 -> clamp 포화 1.0, dt 0.3 => 예산 1.2 -> 1명만 전향.
        SimTuning tuning = DynamicRate(1f);
        string sigCanonical = RunScenario(specs, canonical, 0.3f, tuning);
        string sigCanonicalAgain = RunScenario(specs, canonical, 0.3f, tuning);
        string sigReversed = RunScenario(specs, reversed, 0.3f, tuning);
        string sigShuffled = RunScenario(specs, shuffled, 0.3f, tuning);

        Assert.AreEqual("5->0|", sigCanonical,
            "동적 예산 1 => 최근접 tie에서 낮은 id(5) 1명만 team0으로 전향, 제거 없음 (고정 rate 분기였다면 예산 폭발로 전혀 다른 서명이 된다)");
        Assert.AreEqual(sigCanonical, sigCanonicalAgain, "동일 입력은 동일 결과여야 한다");
        Assert.AreEqual(sigCanonical, sigReversed, "역순(팀 역순 포함) 삽입도 같은 결과여야 한다");
        Assert.AreEqual(sigCanonical, sigShuffled, "셔플 삽입도 같은 결과여야 한다");
    }

    // ---- helpers ----

    // 동적 전향율 테스트용 공통 접촉 배치. team0(승자) 6명이 원점 부근에 밀집하고, team1(패자) 5명 중 팔로워 4명만 접촉한다
    // (리더는 멀리 두어 stage-4 제거를 배제). 접촉 pair 24 >= PairNormalizer 8이라 clamp01이 1.0으로 포화해 strength가
    // 상수 1이 되고, 남는 변수는 전향율 하나뿐이다.
    private static void AddDynamicRateContactLayout(AgentBuffer buffer)
    {
        buffer.Add(0, 0, true, V(0.00f, 0.00f));
        buffer.Add(1, 0, false, V(0.10f, 0.00f));
        buffer.Add(2, 0, false, V(0.20f, 0.00f));
        buffer.Add(3, 0, false, V(0.00f, 0.10f));
        buffer.Add(4, 0, false, V(0.10f, 0.10f));
        buffer.Add(5, 0, false, V(0.20f, 0.10f));
        buffer.Add(6, 1, true, V(50.0f, 50.0f));
        buffer.Add(7, 1, false, V(0.10f, 0.20f));
        buffer.Add(8, 1, false, V(0.20f, 0.20f));
        buffer.Add(9, 1, false, V(0.00f, 0.30f));
        buffer.Add(10, 1, false, V(0.10f, 0.30f));
    }

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

    private static bool ContainsElimination(CombatOutcome outcome, int team, int byTeam)
    {
        for (int i = 0; i < outcome.Eliminations.Count; i++)
        {
            CrowdElimination e = outcome.Eliminations[i];
            if (e.Team == team && e.ByTeam == byTeam)
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
        return RunScenario(specs, order, dt, Default());
    }

    private static string RunScenario(Spec[] specs, int[] order, float dt, SimTuning tuning)
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        for (int k = 0; k < order.Length; k++)
        {
            Spec s = specs[order[k]];
            buffer.Add(s.Id, s.Team, s.Leader, new Vector2(s.X, s.Z));
        }

        SpatialGrid grid = NewGrid(buffer);
        CombatResolver resolver = new CombatResolver(TeamCount, Cap);
        CombatState state = new CombatState(TeamCount);
        CombatOutcome outcome = new CombatOutcome(Cap);
        resolver.Resolve(buffer, grid, tuning, dt, state, outcome);

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
