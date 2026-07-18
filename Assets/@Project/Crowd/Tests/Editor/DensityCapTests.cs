using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// <see cref="SpatialGrid.QueryCircleCapped"/>가 절단이 없는 질의에서 <see cref="SpatialGrid.QueryCircle"/>와
/// 결과·순서까지 같고, 예산 도달 시 스캔 순서 기준 첫 예산개 매칭 후보로 정확히 절단됨을 검증하는 EditMode 테스트다.
/// 위치 생성은 고정 seed를 쓴다.
/// </summary>
[TestFixture]
public sealed class DensityCapTests
{
    private const int Cap = 256;

    /// <summary>
    /// budget=0이면 QueryCircleCapped 결과가 QueryCircle과 순서까지 완전히 같음을 여러 질의로 검증한다(정렬 없이 비교).
    /// </summary>
    [Test]
    public void BudgetZero_MatchesQueryCircle_SameOrder()
    {
        System.Random rng = new System.Random(20240718);
        int count = 150;

        using AgentBuffer buffer = new AgentBuffer(Cap);
        for (int i = 0; i < count; i++)
        {
            float x = (float)(rng.NextDouble() * 50.0 - 25.0);
            float z = (float)(rng.NextDouble() * 50.0 - 25.0);
            buffer.Add(i, i % 4, false, new Vector2(x, z));
        }

        SpatialGrid grid = new SpatialGrid(1.5f, Cap);
        grid.Rebuild(buffer);

        List<int> exact = new List<int>();
        List<int> capped = new List<int>();
        for (int q = 0; q < 24; q++)
        {
            Vector2 center = new Vector2(
                (float)(rng.NextDouble() * 60.0 - 30.0),
                (float)(rng.NextDouble() * 60.0 - 30.0));
            float radius = (float)(rng.NextDouble() * 4.5 + 0.5);

            grid.QueryCircle(center, radius, exact);
            grid.QueryCircleCapped(center, radius, 0, capped);

            CollectionAssert.AreEqual(exact, capped, "query " + q + " (budget=0, r=" + radius + ")");
        }
    }

    /// <summary>
    /// budget이 어떤 질의의 매칭 후보 수보다 크면(절단 없음) 결과가 QueryCircle과 순서까지 같음을 검증한다.
    /// </summary>
    [Test]
    public void BudgetAboveMatched_MatchesQueryCircle_SameOrder()
    {
        System.Random rng = new System.Random(20240719);
        int count = 150;

        using AgentBuffer buffer = new AgentBuffer(Cap);
        for (int i = 0; i < count; i++)
        {
            float x = (float)(rng.NextDouble() * 50.0 - 25.0);
            float z = (float)(rng.NextDouble() * 50.0 - 25.0);
            buffer.Add(i, i % 4, false, new Vector2(x, z));
        }

        SpatialGrid grid = new SpatialGrid(1.5f, Cap);
        grid.Rebuild(buffer);

        List<int> exact = new List<int>();
        List<int> capped = new List<int>();
        for (int q = 0; q < 24; q++)
        {
            Vector2 center = new Vector2(
                (float)(rng.NextDouble() * 60.0 - 30.0),
                (float)(rng.NextDouble() * 60.0 - 30.0));
            float radius = (float)(rng.NextDouble() * 4.5 + 0.5);

            grid.QueryCircle(center, radius, exact);
            grid.QueryCircleCapped(center, radius, count + 100, capped); // 매칭 후보 상한(count)보다 큰 예산 → 절단 없음.

            CollectionAssert.AreEqual(exact, capped, "query " + q + " (budget>count, r=" + radius + ")");
        }
    }

    /// <summary>
    /// 한 cell에 몰린(모두 반경 내) agent에서 결과가 bucket LIFO 체인 순서이고, budget=B가 그 체인의 첫 B개로 절단됨을 검증한다.
    /// </summary>
    [Test]
    public void DenseSingleCell_ReturnsFirstBOfLifoChain()
    {
        const int count = 8;
        using AgentBuffer buffer = new AgentBuffer(Cap);
        for (int i = 0; i < count; i++)
        {
            // 모두 cell (0,0)에 속하고(cellSize=10) center에서 반경 내가 되도록 좁게 배치한다.
            buffer.Add(i, 0, false, new Vector2(3f + 0.1f * i, 5f));
        }

        SpatialGrid grid = new SpatialGrid(10f, Cap);
        grid.Rebuild(buffer);

        Vector2 center = new Vector2(3.35f, 5f);
        const float radius = 2f;

        List<int> exact = new List<int>();
        grid.QueryCircle(center, radius, exact);

        // 삽입 순서 0..count-1을 앞에 prepend하므로 bucket 체인은 내림차순 [count-1 .. 0]이다.
        int[] expectedChain = new int[count];
        for (int i = 0; i < count; i++)
        {
            expectedChain[i] = count - 1 - i;
        }
        CollectionAssert.AreEqual(expectedChain, exact, "밀집 cell 결과는 bucket LIFO 체인 순서여야 한다");

        const int budget = 3;
        List<int> capped = new List<int>();
        grid.QueryCircleCapped(center, radius, budget, capped);

        CollectionAssert.AreEqual(exact.GetRange(0, budget), capped, "budget=B는 체인의 첫 B개로 절단해야 한다");
    }

    /// <summary>
    /// 여러 cell에 걸쳐 모두 반경 내인 배치에서, budget=B 결과가 QueryCircle 결과의 스캔 순서 앞 B개(prefix)와 정확히 같음을 검증한다.
    /// </summary>
    [Test]
    public void BudgetTruncatesToScanOrderPrefix()
    {
        System.Random rng = new System.Random(20240720);
        int count = 40;

        using AgentBuffer buffer = new AgentBuffer(Cap);
        for (int i = 0; i < count; i++)
        {
            // [-1,1]^2에 배치 → 아래 center/radius 반경 내가 보장된다(모든 매칭 후보가 add되어 matched==added).
            float x = (float)(rng.NextDouble() * 2.0 - 1.0);
            float z = (float)(rng.NextDouble() * 2.0 - 1.0);
            buffer.Add(i, 0, false, new Vector2(x, z));
        }

        SpatialGrid grid = new SpatialGrid(0.5f, Cap); // 작은 cell → 여러 cell에 분산.
        grid.Rebuild(buffer);

        Vector2 center = Vector2.zero;
        const float radius = 3f; // [-1,1]^2 전체를 덮음.

        List<int> exact = new List<int>();
        grid.QueryCircle(center, radius, exact);
        Assert.AreEqual(count, exact.Count, "모든 agent가 반경 내여야 한다(테스트 전제)");

        List<int> capped = new List<int>();
        int[] budgets = { 1, 2, 3, 5, 10, 25, count };
        foreach (int b in budgets)
        {
            grid.QueryCircleCapped(center, radius, b, capped);
            int expectedLen = Mathf.Min(b, count);
            CollectionAssert.AreEqual(exact.GetRange(0, expectedLen), capped,
                "budget=" + b + "는 스캔 순서 앞 " + expectedLen + "개여야 한다");
        }
    }

    /// <summary>
    /// budget=1이면 스캔 순서 첫 매칭 후보 하나만 반환함을 검증한다.
    /// </summary>
    [Test]
    public void BudgetOne_ReturnsFirstScannedCandidate()
    {
        System.Random rng = new System.Random(20240721);
        int count = 30;

        using AgentBuffer buffer = new AgentBuffer(Cap);
        for (int i = 0; i < count; i++)
        {
            float x = (float)(rng.NextDouble() * 2.0 - 1.0);
            float z = (float)(rng.NextDouble() * 2.0 - 1.0);
            buffer.Add(i, 0, false, new Vector2(x, z));
        }

        SpatialGrid grid = new SpatialGrid(0.5f, Cap);
        grid.Rebuild(buffer);

        Vector2 center = Vector2.zero;
        const float radius = 3f;

        List<int> exact = new List<int>();
        grid.QueryCircle(center, radius, exact);

        List<int> capped = new List<int>();
        grid.QueryCircleCapped(center, radius, 1, capped);

        Assert.AreEqual(1, capped.Count, "budget=1은 후보 하나만 반환해야 한다");
        Assert.AreEqual(exact[0], capped[0], "budget=1 결과는 스캔 순서 첫 후보여야 한다");
    }

    /// <summary>
    /// 같은 입력·예산으로 반복 실행하면 결과 순서가 매번 동일함을 검증한다(결정론).
    /// </summary>
    [Test]
    public void RepeatedRuns_ProduceIdenticalOrder()
    {
        System.Random rng = new System.Random(20240722);
        int count = 60;

        using AgentBuffer buffer = new AgentBuffer(Cap);
        for (int i = 0; i < count; i++)
        {
            float x = (float)(rng.NextDouble() * 10.0 - 5.0);
            float z = (float)(rng.NextDouble() * 10.0 - 5.0);
            buffer.Add(i, 0, false, new Vector2(x, z));
        }

        SpatialGrid grid = new SpatialGrid(1.5f, Cap);
        grid.Rebuild(buffer);

        Vector2 center = new Vector2(0.5f, -0.5f);
        const float radius = 4f;
        const int budget = 8;

        List<int> first = new List<int>();
        List<int> second = new List<int>();
        grid.QueryCircleCapped(center, radius, budget, first);
        grid.QueryCircleCapped(center, radius, budget, second);

        CollectionAssert.AreEqual(first, second, "동일 입력·예산의 반복 실행은 순서까지 같아야 한다");
    }

    /// <summary>
    /// cell 경계에 걸친 배치에서 budget=0과 절단 없는 큰 budget 모두 QueryCircle과 순서까지 같음을 검증한다.
    /// </summary>
    [Test]
    public void CellBoundary_NoTruncation_MatchesQueryCircle()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        // cellSize=1 기준 x=0, y=0 경계 양쪽에 배치한다.
        Vector2[] pts =
        {
            new Vector2(-0.4f, -0.4f),
            new Vector2(0.4f, -0.4f),
            new Vector2(-0.4f, 0.4f),
            new Vector2(0.4f, 0.4f),
            new Vector2(-0.1f, 0.1f),
            new Vector2(0.1f, -0.1f),
        };
        for (int i = 0; i < pts.Length; i++)
        {
            buffer.Add(i, 0, false, pts[i]);
        }

        SpatialGrid grid = new SpatialGrid(1f, Cap);
        grid.Rebuild(buffer);

        Vector2 center = new Vector2(0f, 0f);
        const float radius = 1.5f;

        List<int> exact = new List<int>();
        grid.QueryCircle(center, radius, exact);

        List<int> capped = new List<int>();
        grid.QueryCircleCapped(center, radius, 0, capped);
        CollectionAssert.AreEqual(exact, capped, "경계 걸침 + budget=0은 QueryCircle과 같아야 한다");

        grid.QueryCircleCapped(center, radius, 1000, capped);
        CollectionAssert.AreEqual(exact, capped, "경계 걸침 + 절단 없는 큰 budget은 QueryCircle과 같아야 한다");
    }
}
