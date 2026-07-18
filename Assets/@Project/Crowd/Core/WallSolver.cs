using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// 정적 도시 벽의 <see cref="WallField"/>(2D signed distance field)에 대해 <c>CharacterController.Move</c>의
/// 실효 계약(캡슐 sweep 차단 · depenetration · 벽 슬라이딩 · skinWidth standoff · minMoveDistance 임계)을
/// 재현하는 단일스레드 결정적 이동 해소 커널이다(Phase C 단계 2).
///
/// 순수 함수이며 상태를 가지지 않는다. 조회는 read-only <see cref="WallField"/>의 Phi/Gradient(순수 float 산술)만
/// 쓰고, 고정된 연산 순서 · 고정된 반복 횟수 · gradient=0 시 고정 no-op fallback으로 완전히 결정적이다.
/// 입력/출력은 world XZ 평면 좌표(Vector2)이며, 유닛-유닛 충돌은 다루지 않는다(커널 steering의 separation 담당).
///
/// 사용법: solver는 최종 world XZ 위치를 반환한다. 소유자는 이를 같은 transform.position에 써서
/// 이후의 walkable clamp · Y-pin · 팔로워 되먹임(실제 변위 기반)이 CC.Move 경로와 동일하게 동작하게 한다.
/// </summary>
public static class WallSolver
{
    /// <summary>명령 이동량이 이 값 미만이면 이동하지 않는다(CC.Move minMoveDistance 재현, 단위 m).</summary>
    public const float MinMoveDistance = 0.001f;

    /// <summary>sweep 이전 시작점 depenetration 최대 반복 횟수(겹침 복구).</summary>
    public const int StartDepenIters = 4;

    /// <summary>sweep 이후 최종 depenetration 최대 반복 횟수(코너 정착).</summary>
    public const int FinalDepenIters = 4;

    /// <summary>각 sweep 서브스텝에서 isosurface 되밀기(다중 contact) 최대 반복 횟수(코너: 두 벽).</summary>
    public const int SweepContactIters = 3;

    /// <summary>서브스텝 길이를 이 셀 배수 이하로 유지해 tunneling을 방지한다.</summary>
    public const float SubStepCellFraction = 0.5f;

    /// <summary>
    /// world XZ 시작점 <paramref name="p0"/>에서 명령 변위 <paramref name="delta"/>만큼 이동하되, 벽 SDF 표면에서
    /// <paramref name="clearance"/>(= (radius+skinWidth)*scale, standoff 재현) 이상 떨어지도록 해소한 최종 world XZ 위치를 반환한다.
    /// ① minMove 임계 → ② 시작점 depenetration → ③ sweep 전진 + isosurface 되밀기(법선성분 제거 = 접선 슬라이딩)
    /// → ④ 최종 depenetration. walkable clamp/Y-pin은 소유자가 반환값을 쓴 뒤 기존 코드로 수행한다.
    /// </summary>
    /// <param name="field">read-only로 로드된 벽 거리장. 반드시 <see cref="WallField.IsLoaded"/>여야 한다.</param>
    /// <param name="p0">이동 전 world XZ 위치.</param>
    /// <param name="delta">이번 step의 명령 world XZ 변위.</param>
    /// <param name="clearance">벽 표면에서 유지할 최소 거리(m). per-agent scale이 이미 곱해진 값.</param>
    /// <returns>해소 후 world XZ 위치. 실제 변위는 (반환값 - p0)이며, 명령 변위가 아니다.</returns>
    public static Vector2 Resolve(WallField field, Vector2 p0, Vector2 delta, float clearance)
    {
        // 관리형 경로는 blittable 뷰 오버로드로 위임한다(산술 단일 원천). 병렬 job은 이제 Burst(FloatMode.Strict)라 이 관리형 경로와 near-Mono지만 bit-identical하지는 않다.
        return Resolve(field.AsView(), p0, delta, clearance);
    }

    /// <summary>
    /// <see cref="Resolve(WallField,Vector2,Vector2,float)"/>와 <b>동일한</b> 이동 해소를 blittable <see cref="WallFieldView"/>로 수행한다.
    /// job/Burst에서 <see cref="WallField"/> 관리형 참조 없이 호출하기 위한 오버로드다. 산술·연산 순서·반복 횟수·fallback이 모두 동일하다.
    /// </summary>
    public static Vector2 Resolve(in WallFieldView field, Vector2 p0, Vector2 delta, float clearance)
    {
        float2 d0 = new float2(delta.x, delta.y);
        float deltaLen = math.length(d0);

        // ① minMoveDistance 재현: 명령 이동량이 임계 미만이면 원위치.
        if (deltaLen < MinMoveDistance)
        {
            return p0;
        }

        // bilinear 과대평가에 대한 보수적 tunneling 보정: 저장 SDF는 참 거리이므로 조회 거리에서 bias만큼 빼는 것과
        // 동치인 effective clearance = clearance + bias를 쓴다(Stage 0/1이 asset에 남긴 권장 bias 반영).
        float effClearance = clearance + field.BilinearBias;

        float2 p = new float2(p0.x, p0.y);

        // ② 시작점 depenetration(스폰/분리 오차로 파고든 상태를 표면 밖으로 복구).
        Depenetrate(field, ref p, effClearance, StartDepenIters);

        // ③ swept 전진(tunnel 방지: 서브스텝 <= 0.5셀). 서브스텝마다 isosurface로 되밀어 법선성분을 제거하면
        //    접선(벽 따라) 성분이 보존되어 자연스러운 슬라이딩이 된다. 코너(두 벽)는 다중 contact 반복으로 수렴한다.
        float cell = field.CellSize;
        int steps = (int)math.ceil(deltaLen / (SubStepCellFraction * cell));
        if (steps < 1)
        {
            steps = 1;
        }

        float2 ds = d0 / steps;
        for (int s = 0; s < steps; s++)
        {
            p += ds;
            for (int c = 0; c < SweepContactIters; c++)
            {
                float dist = field.Phi(p.x, p.y);
                if (dist >= effClearance)
                {
                    break;
                }

                float2 n = field.Gradient(p.x, p.y);
                if (n.x == 0f && n.y == 0f)
                {
                    break; // medial-axis/평탄: 밀어낼 방향 없음(결정적 no-op).
                }

                p += n * (effClearance - dist);
            }
        }

        // ④ 최종 depenetration(sweep 종료 후 코너/겹침 정착).
        Depenetrate(field, ref p, effClearance, FinalDepenIters);

        return new Vector2(p.x, p.y);
    }

    // 표면 밖 effClearance까지 gradient 방향으로 밀어내는 고정 반복 depenetration. gradient=0이면 결정적으로 중단한다.
    private static void Depenetrate(in WallFieldView field, ref float2 p, float effClearance, int iters)
    {
        for (int k = 0; k < iters; k++)
        {
            float dist = field.Phi(p.x, p.y);
            if (dist >= effClearance)
            {
                break;
            }

            float2 n = field.Gradient(p.x, p.y);
            if (n.x == 0f && n.y == 0f)
            {
                break;
            }

            p += n * (effClearance - dist);
        }
    }
}
