using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEditor;

/// <summary>
/// ResourcePathValidator의 WallSdf 무결성 검사(항목 6)에 대한 EditMode 회귀 테스트다.
/// 검사가 실제로 잡아내는지를 보려면 같은 길이의 변형 payload를 넣어 봐야 하므로, 검사 진입점
/// <c>CheckWallSdfIntegrity</c>를 직접 호출한다(전체 Validate는 이 프로젝트의 다른 계약까지 함께 본다).
///
/// 이 파일이 Game/Editor에 있는 이유: 검증기와 WallFieldBaker.Crc32는 predefined 어셈블리
/// Assembly-CSharp-Editor에 있고, asmdef 어셈블리(Crowd/Tests/Editor)는 predefined 어셈블리를 참조할 수 없다.
/// 같은 배치의 선례가 Game/Editor/CameraRootLifecycleTests.cs, City/Editor/CityBuildingsGeneratorTests.cs다.
/// </summary>
public sealed class ResourcePathValidatorWallSdfTests
{
    private const string WallSdfAssetPath = "Assets/@Project/City/Generated/WallSdf.asset";

    // CRC-32/ISO-HDLC 표준 체크 벡터("123456789" -> 0xCBF43926). 테이블/초기값/최종 XOR이 바뀌면 여기서 먼저 깨진다.
    [Test]
    public void Crc32_StandardCheckVector_Matches()
    {
        Assert.That(WallFieldBaker.Crc32(Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xCBF43926u));
    }

    [Test]
    public void WallSdfIntegrity_ShippingAsset_HasNoFailures()
    {
        WallSdfAsset asset = LoadAsset();
        List<string> failures = new List<string>();

        ResourcePathValidator.CheckWallSdfIntegrity(asset, asset.Payload.bytes, failures);

        Assert.That(failures, Is.Empty, string.Join("; ", failures));
    }

    [Test]
    public void WallSdfIntegrity_SameLengthSingleByteMutation_FailsOnCrc()
    {
        WallSdfAsset asset = LoadAsset();

        // TextAsset.bytes는 접근마다 새 배열을 돌려주므로, 이 변형은 asset을 건드리지 않는다.
        byte[] mutated = asset.Payload.bytes;
        mutated[mutated.Length / 2] ^= 0x01;

        List<string> failures = new List<string>();
        ResourcePathValidator.CheckWallSdfIntegrity(asset, mutated, failures);

        // 길이·해석 파라미터는 그대로이므로 CRC 항목 하나만 실패해야 한다(길이 검사만으로는 못 잡는 변형).
        Assert.That(mutated.Length, Is.EqualTo(asset.CellCount * sizeof(float)));
        Assert.That(failures, Has.Count.EqualTo(1), string.Join("; ", failures));
        Assert.That(failures[0], Does.Contain("CRC32"));
    }

    private static WallSdfAsset LoadAsset()
    {
        WallSdfAsset asset = AssetDatabase.LoadAssetAtPath<WallSdfAsset>(WallSdfAssetPath);
        Assert.That(asset, Is.Not.Null, WallSdfAssetPath);
        Assert.That(asset.Payload, Is.Not.Null, WallSdfAssetPath);
        return asset;
    }
}
