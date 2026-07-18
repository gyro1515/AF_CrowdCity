using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// <see cref="SpatialGrid"/>의 원형 질의가 brute force와 일치하는지, <see cref="FollowerSteering"/>의 슬롯 오프셋이
/// 단조 증가·상호 구별되는지 검증하는 EditMode 테스트다. 위치 생성은 고정 seed를 쓴다.
/// </summary>
[TestFixture]
public sealed class GridAndSteeringTests
{
    private const int Cap = 256;

    /// <summary>
    /// 고정 seed의 무작위 위치 위에서 QueryCircle 결과가 brute force 거리 판정과 정확히 같음을 여러 질의로 검증한다.
    /// </summary>
    [Test]
    public void QueryCircle_MatchesBruteForce_OnRandomData()
    {
        System.Random rng = new System.Random(20240714);
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

        List<int> queryResult = new List<int>();
        var positions = buffer.Pos;

        for (int q = 0; q < 24; q++)
        {
            Vector2 center = new Vector2(
                (float)(rng.NextDouble() * 60.0 - 30.0),
                (float)(rng.NextDouble() * 60.0 - 30.0));
            float radius = (float)(rng.NextDouble() * 4.5 + 0.5);

            grid.QueryCircle(center, radius, queryResult);

            List<int> expected = new List<int>();
            float radiusSq = radius * radius;
            for (int i = 0; i < count; i++)
            {
                float dx = positions[i].x - center.x;
                float dy = positions[i].y - center.y;
                if (dx * dx + dy * dy <= radiusSq)
                {
                    expected.Add(i);
                }
            }

            List<int> actual = new List<int>(queryResult);
            expected.Sort();
            actual.Sort();
            CollectionAssert.AreEqual(expected, actual, "query " + q + " (r=" + radius + ")");
        }
    }

    /// <summary>
    /// QueryCircle이 넘겨받은 결과 리스트를 먼저 비운 뒤 채움을 검증한다.
    /// </summary>
    [Test]
    public void QueryCircle_ClearsResultsFirst()
    {
        using AgentBuffer buffer = new AgentBuffer(Cap);
        buffer.Add(0, 0, false, new Vector2(100f, 100f)); // 질의 반경 밖.

        SpatialGrid grid = new SpatialGrid(1.5f, Cap);
        grid.Rebuild(buffer);

        List<int> results = new List<int> { 42, 43, 44 };
        grid.QueryCircle(new Vector2(0f, 0f), 1f, results);

        Assert.AreEqual(0, results.Count, "이전 내용은 지워지고 반경 밖 agent는 담기지 않아야 한다");
    }

    /// <summary>
    /// 슬롯 반경이 슬롯 인덱스에 따라 단조 증가하고, 모든 슬롯 오프셋이 서로 구별됨을 검증한다.
    /// </summary>
    [Test]
    public void SlotOffset_RadiusMonotonic_AndOffsetsDistinct()
    {
        const float spacing = 0.6f;
        const int slotCount = 25;

        Vector2[] offsets = new Vector2[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            offsets[i] = FollowerSteering.SlotOffset(i, spacing);
        }

        // 첫 슬롯 반경 = spacing * sqrt(1) = spacing.
        Assert.AreEqual(spacing, offsets[0].magnitude, 1e-4f);

        // 반경 단조 증가.
        for (int i = 1; i < slotCount; i++)
        {
            Assert.Greater(offsets[i].magnitude, offsets[i - 1].magnitude,
                "슬롯 " + i + "의 반경이 이전보다 커야 한다");
        }

        // 오프셋 상호 구별.
        for (int i = 0; i < slotCount; i++)
        {
            for (int j = i + 1; j < slotCount; j++)
            {
                float separation = (offsets[i] - offsets[j]).magnitude;
                Assert.Greater(separation, 1e-3f, "슬롯 " + i + "과 " + j + "의 오프셋이 겹치면 안 된다");
            }
        }
    }
}
