using UnityEngine;

/// <summary>
/// 팔로워 배치 슬롯 계산을 담당하는 순수 계산 유틸리티다.
/// 리더 기준 XZ 평면의 golden-angle 나선 오프셋을 제공한다.
/// </summary>
public static class FollowerSteering
{
    // golden angle 근사값(라디안)이다.
    private const float GoldenAngleRad = 2.39996f;

    /// <summary>
    /// <paramref name="slotIndex"/>번째 팔로워 슬롯의 리더 기준 XZ 오프셋을 반환한다. slotIndex 0이 첫 팔로워다.
    /// r = spacing * sqrt(slotIndex + 1), theta = slotIndex * 2.39996f 인 golden-angle 나선이다.
    /// </summary>
    public static Vector2 SlotOffset(int slotIndex, float spacing)
    {
        float radius = spacing * Mathf.Sqrt(slotIndex + 1f);
        float theta = slotIndex * GoldenAngleRad;
        return new Vector2(radius * Mathf.Cos(theta), radius * Mathf.Sin(theta));
    }
}
