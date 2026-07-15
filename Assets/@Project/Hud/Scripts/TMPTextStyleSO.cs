using UnityEngine;

/// <summary>
/// TMP text의 face와 outline 두께 원본 설정을 담는 읽기 전용 스타일 SO다.
/// 런타임 material과 현재 표시 상태는 보관하지 않는다.
/// </summary>
[CreateAssetMenu(menuName = "AF/CrowdCity/TMP Text Style", fileName = "TMPTextStyle")]
public sealed class TMPTextStyleSO : ScriptableObject
{
    [SerializeField] private Color faceColor = Color.white;
    /// <summary>텍스트 전면 색상이다.</summary>
    public Color FaceColor => faceColor;

    [Range(0f, 1f)]
    [SerializeField] private float outlineWidth = 0.1f;
    /// <summary>TMP shader에 전달할 외곽선 두께(0~1)다.</summary>
    public float OutlineWidth => outlineWidth;

    private void OnValidate()
    {
        outlineWidth = Mathf.Clamp01(outlineWidth);
    }
}
