# AF_CrowdCity

수천~수만 개체가 동시에 상호작용하는 군중 시뮬레이션을 Unity에서 **의도적으로 유니티 기본 방식으로 먼저 구현하고**, 무너지는 지점을 실측으로 하나씩 대체해 간 개인 프로젝트다.

목적은 게임이 아니라 **측정**이다. 두 가지를 동시에 시험했다 — ① 내가 세운 구조 설계가 규모가 커질 때 버티는가, ② 코드를 전부 AI가 쓰는 작업 흐름이 어디까지 가는가.

---

## ⚠️ 이 저장소에는 모델·애니메이션 에셋이 들어 있지 않다

사내 게임잼에서 **제공받은** 에셋이라 저작권이 이 저장소에 없다. 원본 임포트 파일과, 에디터 도구가 그것을 구워 만든 파생 산출물을 **모두** 제외했다. 파생물은 원본 지오메트리·애니메이션을 그대로 담고 있어 원본만 빼는 것으로는 제외가 되지 않는다.

| 비어 있는 경로 | 무엇이 빠졌나 |
|---|---|
| `Assets/@Project/City/Externals/` | `City.fbx` (도시 결합 메시), `city_atlas.png` (도시 텍스처) |
| `Assets/@Project/Human/Externals/` | `Human_Base.fbx` (휴머노이드 리그 + `HumanWalk` 클립) |
| `Assets/@Project/City/Generated/BuildingMeshes/` | 위 `City.fbx`의 Buildings 메시를 37개로 쪼갠 산출물 |
| `Assets/@Project/Human/VAT/HumanWalkVat*.asset` | 위 모델·클립을 프레임별로 구운 Vertex Animation Texture |

**클론하면 씬과 프리팹의 참조가 깨진 상태로 열린다.** 제외한 43개 에셋의 guid를 추적 파일 전체에 대조해 확인한 영향 범위는 다음 7곳이다 — `Scenes/GameScene.unity`, `Human/Prefabs/Human.prefab`, `Human/Animations/HumanWalk.controller`(모션 참조), `Human/VAT/HumanVat.mat`, `Crowd/Resources/Prefabs/CrowdRoot.prefab`, `City/Prefabs/GeneratedBuildings/Building_00~36.prefab`, `City/Materials/City_Occluded.mat`.

**포함된 것**: 런타임·에디터 코드 전부, 두 생성기(`CityBuildingsGenerator`, `HumanVatBaker`)와 베이커·검증기·측정 하네스 전부, VAT 셰이더, 문서, 그리고 `City/Generated/WallSdf.asset`/`.bytes`(콜라이더에서 구운 거리장 — 원본 지오메트리로 복원되지 않는 공간 가속 구조라 남겨 두었다).

### 대체하려면

1. **휴머노이드 리그 모델 1종**을 `Assets/@Project/Human/Externals/Human_Base.fbx`로 넣는다. 걷기 클립 이름은 `HumanWalk`여야 하고 **루핑(loop-close)이어야 한다** — 셰이더에 modulo wrap 경로 하나만 있어서, 루핑이 아닌 클립은 베이커가 예외를 던져 베이크 자체를 거부한다.
2. **도시 메시 1종**을 `Assets/@Project/City/Externals/City.fbx`로 넣는다. 결합 메시 오브젝트 이름은 `Buildings`다.
   ⚠️ `CityBuildingsGenerator`가 원본 메시의 identity를 상수로 못 박아 두었다(`ExpectedBuildingCount = 37`, `ExpectedSourceVertexCount = 12240`, `ExpectedSourceTriangleCount = 6120`). **다른 메시를 넣으면 생성기가 "원본과 다르다"며 사용자 데이터를 지우지 않고 중단한다** — 의도된 게이트이므로, 교체 시 이 상수들을 새 메시 값으로 함께 고쳐야 한다.
3. `Human.prefab`의 SkinnedMeshRenderer를 새 모델에 연결한 뒤, 에디터 메뉴에서 순서대로 실행한다:
   `AF/CrowdCity/Setup City Buildings` → `Bake Human Prefab` → `Bake Human VAT` → `Bake Crowd Renderer` → `Bake Wall SDF`
4. `AF/CrowdCity/Validate Resource Paths`와 `Validate Game Scene`이 남은 누락을 짚어 준다.

---

## 작업 조건 — 이 저장소가 실제로 측정한 것

- **사내 게임잼에서 시작했다.** 주제 선정 기준은 "더 많은 객체가 상호작용하는 것"이었다. 회사가 시작 시점부터 개인 저장소로 만들라고 지시해 처음부터 내 깃에 있다.
- **코드 작성은 전부 AI다.** 회사를 다니며 밤에만 진행했다.
- **전체 코드 리뷰는 하지 않았다.** 사람이 본 것은 병목 구간과 전체 구조뿐이다. 다만 **무엇을 어떻게 할지의 선택은 전부 사람이 했다.**
- 그래서 이 저장소가 답하려는 질문은 "AI가 코드를 잘 쓰는가"가 아니라, **그 조건에서 성립하려면 작업 흐름에 무엇이 있어야 하는가**다. 저장소의 `AGENTS.md`/`CLAUDE.md`, `Docs/WORK_STATE.md`(현재 상태·불변식·게이트·함정), `Docs/PROJECT_MAP.md`(구조 좌표)가 그 답으로 만들어진 장치다.

## 측정 규범

수치를 믿을 수 있게 만드는 쪽에 대부분의 노력이 들어갔다.

- **시드 고정 오라클 바이트 동일 게이트.** 시뮬레이션 결과가 바뀌면 안 되는 변경은 스냅샷 md5가 바이트 단위로 같아야 통과한다.
- **A/B/B/A 교차 순서.** 측정 순서가 결과에 실리는 것을 막는다.
- **arm 내 편차를 못 넘는 차이는 '판정 불가'로 남긴다.** 개선으로 세지 않는다.
- **근거 없는 수치는 지우지 않고 철회 사유와 함께 남긴다.** 지우면 다음 사람이 같은 수치를 다시 유도하기 때문이다. `Docs/WORK_STATE.md`에 철회된 수치와 그 추적 기록이 그대로 있다.
- **Codex ↔ Claude 교차검증.** 검토 측에 앞선 대화 맥락을 넘기지 않고 산출물만 준다. 사정을 아는 쪽이 검토하면 "그럴 만했다"가 미리 깔려 자기 확인이 되기 때문이다.

## 측정 결과

⚠️ **아래 전부 데스크톱 Unity 에디터 + Mono 수치다.** Unity 6000.3.9f1, Ryzen 5 5600X(6C/12T) / RTX 4080, D3D11.
**플레이어 빌드가 아니고, IL2CPP/AOT가 아니고, 실기기가 아니다.** 부호는 옮겨가지만 크기는 그대로 옮겨가지 않는다.

**1만 에이전트 — GPU 인스턴싱 경로에서 틱당 9.56 ms**
3런 중앙값이다(9.21 / 9.56 / 10.62). **GPU 경로 한정** 수치이고, 같은 하네스·같은 질문을 SkinnedMeshRenderer 경로에서 재면 20.56 ms다(−53.5%).
이 구간에는 **렌더 비용이 들어 있지 않다** — 하네스가 `SimTick`만 돌리고 `RenderInterpolate`를 호출하지 않는다. 50Hz 고정 스텝의 틱 주기 20 ms는 **성능 목표나 수용 임계가 아니라 루프 케이던스라는 구조적 사실**이므로, 이 수치를 "프레임 예산의 몇 %"로 읽으면 안 된다.

**A/B 판정 — GPU 경로 `StepSim` 틱당 −12.83 ms (−55.5%)**
동일-arm 최대 spread의 **7.99배**라 판정 규칙을 통과했다. 이 값은 구간 `[−20.76, −12.83]`의 **보수적 끝**이다(`RenderInterpolate` 1회가 분할 상환되어 들어 있고, 상환 제수가 arm마다 다르다).
⚠️ **Δ만 인용해야 한다.** 절대값 23.113 → 10.287은 하네스가 `SeparationVisitBudget = 48`을 강제한 값이고 출하 설정은 0이라, 게임이 도는 설정에서 나올 수치가 아니다. 두 arm이 같은 override를 공유하므로 **델타와 판정은 유효하고, 무효인 것은 절대값이다.**
⚠️ 이 이득의 출처는 **T1a — 죽은 `transform.rotation` 쓰기 6곳을 게이팅한 1파일 +26/−10 변경**이다. 앞선 5단계 리팩터(P3)의 성과가 아니다. P3 커밋들은 BEFORE arm에도 전부 들어 있어 기여분이 0이라는 것을 `git merge-base --is-ancestor`로 확인했다.

**벽 SDF 산출물 — 실측**
`MeshCollider` 40개 → **848,192셀**(928 × 914), 셀 크기 0.1 m, payload **3,392,768 bytes**. 저작 시점에 CRC·길이·해석 파라미터 7개·schemaVersion을 핀으로 박아 두어, 재베이크가 이 중 하나라도 바꾸면 검증기가 설계상 실패한다.

**결정성 게이트 — 그 조합·그 창에서 바이트 동일 확인**
`n=2000 / seed=12345 / 1000틱` **1조합**에 대해 스냅샷 md5 `4F79282EB20A79023B45F2EB2DE5271B`가 두 커밋·두 출하 config에 걸쳐 재현됐다.
⚠️ 커버리지 경계를 함께 읽어야 한다 — 같은 n/seed로 5000틱 진단을 돌리면 **최초로 관측되는 팀 인원수 감소가 틱 1092**로, 게이트가 멈추는 1000틱보다 92틱(시뮬레이션 시간 1.84초) 뒤다. 즉 이 창 안에는 전향·제거가 0건이다.

## 결론

구현한 뒤 실측으로 병목을 하나씩 걷어내는 방식도 유효하다 — 위 수치가 그 결과다.
다만 **일정 규모 이상의 오브젝트 상호작용이 목표라면 처음부터 DOTS로 설계해야 한다.**

> 어느 지점에서 DOTS로 쉽게 갈아탈 수 있을 줄 알았는데 그렇지 않았고, 그때 바꿔야 하는 건 자료구조가 아니라 설계 전체였다.

같은 게임을 DOTS로 재구현해 **같은 게이트로** 비교하는 것이 다음 계획이다(미착수).

## 문서

| 문서 | 답하는 질문 |
|---|---|
| [`Docs/WORK_STATE.md`](Docs/WORK_STATE.md) | 지금 어떤 상태인가 — 불변식 · 검증 게이트 · 함정 · **철회된 수치와 그 추적 기록** |
| [`Docs/PROJECT_MAP.md`](Docs/PROJECT_MAP.md) | 어디에 있는가 — 구조 좌표 |
| [`Docs/CrowdCity/CROWD_GUIDE.md`](Docs/CrowdCity/CROWD_GUIDE.md) | 왜 그렇게 했는가 |
| [`Docs/CrowdCity/Perf/MANIFEST.md`](Docs/CrowdCity/Perf/MANIFEST.md) | 측정 증거 — 환경 · 커맨드라인 · 부하 통제 · 판정 규칙 · 판독 주의 |
| [`Docs/CrowdCity/Perf/oracle_baseline_n2000_s12345.md`](Docs/CrowdCity/Perf/oracle_baseline_n2000_s12345.md) | 결정성 게이트의 체크섬 근거와 커버리지 경계 |
| [`AGENTS.md`](AGENTS.md) / [`CLAUDE.md`](CLAUDE.md) | AI가 이 저장소에서 지켜야 하는 규칙 (두 파일은 같은 문서의 두 독자용 사본) |

**성능 수치를 인용할 때는 반드시 경로(GPU / SMR)를 함께 적을 것.** 같은 하네스·같은 세그먼트의 두 데이터셋이 있고, 경로를 떼면 두 배 이상 차이 나는 값이 뒤섞인다.

---

*Unity 6000.3.9f1 · URP · Burst / Collections / Mathematics · Input System*
