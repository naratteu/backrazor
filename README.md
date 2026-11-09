# BackRazor

Razor 컴포넌트가 자신의 상태·작업·갱신·자식 객체의 수명을 소유하도록 하는 라이브러리입니다. 관찰자는 그 렌더 결과를 읽습니다. 브라우저가 없어도 컴포넌트는 타이머와 이벤트로 스스로 갱신하고 계속 운영됩니다.

- `src/BackRazor`: 재사용 라이브러리. `Root<TComponent,TItem>`, 읽기 전용 `View`, R3 변경 알림과 `Observer<TValue>`. MQTT 의존성은 없습니다.
- `BackRazor.Contracts`, `BackRazor.Analyzers`: `[BackRazor]` 계약과 Razor 소스 위치로 보고하는 분석기.
- `examples/BackRazor.EdgeNode`: 설정 파일로 디바이스를 구성하는 Sparkplug B 노드. MQTT 연결과 디바이스 작업은 Node/Device 컴포넌트가 직접 소유합니다. 라이브러리 Observer의 읽기 콜백과 ChildContent에서 표시 방식을 정합니다.

## 실행

.NET SDK는 `global.json`의 **11.0.100-rc.3.26522.148**로 고정했습니다. 현재 작업 환경에는 `.dotnet/`에 설치되어 있습니다. 새 환경에서는 [공식 설치 스크립트](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script)로 해당 버전을 저장소의 `.dotnet` 폴더에 설치합니다.

```sh
# 저장소 루트에서: 선택 사항인 로컬 테스트 브로커
docker compose up -d broker
# 상태 관찰 페이지 + 실제 MQTT 노드
scripts/run-edge.sh
```

[http://127.0.0.1:5080](http://127.0.0.1:5080)을 엽니다. 여러 브라우저가 같은 Node/Device 인스턴스 ID와 측정값을 봅니다. 탭을 닫으면 관찰만 종료되고 운영 객체는 계속 동작합니다.

`examples/BackRazor.EdgeNode/node.json`을 편집하면 250ms 간격으로 감지합니다. 샘플 `node.changed.json`은 `sensor-b`를 유지하고 `pump-a`를 `pump-c`로 교체합니다. 별도 파일은 다음처럼 주입합니다.

```sh
scripts/run-edge.sh --config /absolute/path/node.json --state-dir /absolute/path/state
```

설정은 `broker`와 `devices`로 구성합니다. device의 `id`는 유일해야 합니다. `intervalMs`는 50..60000이며, 선택적인 `stopDelayMs`는 비동기 정리를 확인하기 위한 0..5000ms 지연입니다. **모든 값이 같은 디바이스는 유지**하고, 같은 ID라도 설정값이 달라지면 이전 객체를 정리한 뒤 새 객체를 만듭니다. 잘못된 JSON·중복 ID·설정 파일 일시 유실은 기존 구성을 보존하고 페이지에 오류를 표시합니다. broker/node 식별자 변경은 프로세스 재시작이 필요합니다. 파일 교체는 임시 파일 작성 후 rename하는 방식을 권장합니다.

브로커가 없어도 디바이스의 로컬 동작과 관찰은 계속됩니다. `/healthz`는 설정/루트 오류를 표시하는 운영 상태 검사이며, MQTT 연결 여부는 페이지의 `MQTT` 상태를 확인합니다. HTTP는 기본 loopback에만 바인딩하며 관찰 페이지에 인증은 없습니다.

## 수명 보장

```razor
@attribute [BackRazor]
@foreach (var spec in Items)
{
    <Device @key="spec.Id" Spec="spec" />
}
```

```csharp
await using var root = new Root<Node, DeviceSpec>(scopes, logs, nameof(Node.Items), x => x.Id);
await root.ApplyAsync(configuration.Devices);
// 브라우저에는 root.View만 전달
```

전환은 **기존/신규 구성의 교집합 렌더 → 해당 업데이트 완료 대기 → 신규 구성 렌더**입니다. 교집합은 ID와 값이 모두 같은 항목입니다. 제거되는 자식의 `DisposeAsync`가 전부 완료된 후 새 자식이 생성됩니다. Renderer 내부 Diff나 Dispose 큐를 수정하지 않고, 소규모 `StaticHtmlRenderer` 파생 클래스가 기존 업데이트 완료 작업을 기다립니다. `@key`만으로 이 순서가 보장되지는 않습니다.

원본 인스턴스를 별도 dictionary로 관리하지 않습니다. Renderer가 인스턴스를 소유하고, 라이브러리는 비교용 설정값만 보관합니다. 정리 실패는 루트를 fault 상태로 만들어 이후 생성을 차단합니다. 시작된 정리는 취소 토큰으로 버리지 않으며, 전체 종료는 **자식 정리 → 루트 정리 → DI scope 정리** 순서입니다.

이 보장은 불변 값 설정, 유일한 key, 고정된 소유자 컴포넌트 타입, `ApplyAsync`를 통한 구성 변경을 전제로 합니다. 컴포넌트가 작업을 fire-and-forget으로 버리거나 임의로 자식 구성을 바꾸면 보장할 수 없습니다. 종료하지 않는 `DisposeAsync`는 전환도 정지시키므로 컴포넌트의 네트워크 작업에 제한 시간을 둬야 합니다.

## Sparkplug B 예제

HiveMQtt 0.47.1의 MQTT 전송과 Sparkplug 페이로드 codec을 사용합니다. 상위 `SparkplugEdgeNode`의 별도 디바이스 저장소와 알려진 QoS/Will 문제를 가져오지 않도록, Node 컴포넌트 안에서 순서와 세션을 관리합니다.

- NBIRTH/DBIRTH/DDATA/DDEATH는 QoS 0, retain=false. NDEATH는 QoS 1, bdSeq만 포함합니다.
- 발행을 직렬화하고 seq를 공유합니다. 재접속과 NCMD Rebirth에서 NBIRTH seq=0으로 시작하며, 각 디바이스는 새 세션의 DBIRTH 이후 DDATA를 보냅니다.
- bdSeq를 `--state-dir`에 저장하고 세션마다 증가시킵니다. 프로세스 비정상 종료는 브로커 Last Will로 알립니다. 동일 group/node는 한 프로세스만 운영해야 합니다.
- `Node Control/Rebirth` NCMD, `Device Control/Reset` DCMD를 지원합니다. 종료 시 자식의 DDEATH를 완료한 후 노드 연결을 정리합니다.

디바이스 값은 현재 `Math.Sin` 기반 **모의 측정**입니다. 물리 장치 드라이버, Primary Host/STATE, 메트릭 alias/template/dataset 등은 이 예제에 구현하지 않았습니다. 실제 MQTT 통신·복구·수명 순서를 검증한 예제이며 Sparkplug TCK 전체 적합성 인증을 의미하지 않습니다.

## NativeAOT

```sh
scripts/publish-native.sh osx-arm64
cd artifacts/native
./BackRazor.EdgeNode --urls http://127.0.0.1:5080
```

이 환경에서 macOS ARM64 네이티브 바이너리의 실제 브라우저 Circuit 연결과 MQTT 동작을 검증했습니다. .NET 런타임 별도 설치 없이 실행하며, 바이너리는 약 **27MiB**입니다. 정적 웹 자산과 설정 파일도 함께 배포해야 하므로 실행 파일 하나만 복사하면 관찰 페이지가 동작하지 않습니다. dSYM은 배포에 필요하지 않습니다.

.NET 11 Blazor NativeAOT는 아직 **실험적 경로**입니다. [공식 설명](https://devblogs.microsoft.com/aspire/aspire-dashboard-native-aot/)과 [메타데이터 확장 PR](https://github.com/dotnet/aspnetcore/pull/68307)을 기준으로, 닫힌 컴포넌트 그래프의 메타데이터를 보존하고 Circuit/HTTP JSON 소스 생성을 등록했습니다. publish의 프레임워크 trim/AOT 경고는 숨기지 않았으며 `artifacts/native-publish.log`에서 확인할 수 있습니다. 임의 컴포넌트나 다른 OS/CPU까지 검증한 것은 아닙니다. Linux 배포는 해당 타깃의 지원 빌드 환경에서 publish와 같은 수용 테스트를 실행해야 합니다.

## 패키지와 분석기

```sh
.dotnet/dotnet pack BackRazor.Contracts/BackRazor.Contracts.csproj -c Release -o artifacts/packages
.dotnet/dotnet pack src/BackRazor/BackRazor.csproj -c Release -o artifacts/packages
```

소비자는 `BackRazor` **0.3.0-preview.1** 하나를 참조합니다. `BackRazor.Contracts`는 전이 의존성으로 따라오며 분석기도 함께 제공됩니다. 패키지는 [GitHub Releases](https://github.com/naratteu/backrazor/releases)에서 받습니다. 아래 설치 방법은 다운로드한 패키지를 로컬 소스로 사용합니다. 별도의 컴포넌트 라이브러리도 표시한 컴포넌트를 정의하는 프로젝트에서 BackRazor 또는 Contracts 패키지를 직접 참조합니다. Roslyn은 실행 의존성에 포함되지 않습니다.

| 진단 | 강제하는 계약 |
| --- | --- |
| BRZ001 | ComponentBase 상속 |
| BRZ002 | disposable 자원 필드가 있으면 정리 인터페이스 구현 |
| BRZ003 | JS/HTTP/Circuit/Navigation/브라우저 관련 타입 의존 금지 |
| BRZ004 | OnAfterRender 계열 금지 |
| BRZ005 | 렌더 및 분석 가능한 호출 경로에서 상태 쓰기 금지 |
| BRZ006 | 렌더의 검증 불가능한 외부·동적·다형성 호출 거부 |
| BRZ007 | const 외 static 필드 금지 |

명령·초기화는 상태를 변경할 수 있습니다. Razor 생성 코드를 분석하고 원래 `.razor`/`.razor.cs` 위치로 빌드 진단을 보고합니다. 실제 에디터 밑줄은 Razor 진단 통합에 따릅니다. 분석기는 자원 해제의 완전성이나 C# 전체의 순수성을 증명하지 않으며, 임의 자식 트리와 악의적인 억제를 막는 샌드박스가 아닙니다.

## 검증

Node.js, Docker, Chromium이 필요합니다. 테스트용 브로커는 별도 컨테이너와 임의의 loopback 포트를 사용하고 종료 시 제거합니다.

```sh
npm ci --prefix tests
npx --prefix tests playwright install chromium
npm test --prefix tests
# publish된 실제 네이티브 실행 파일로 동일한 브라우저/MQTT 테스트
BACKRAZOR_NATIVE="$PWD/artifacts/native/BackRazor.EdgeNode" node tests/edge.mjs
```

코어 테스트는 점유 중인 자원의 비동기 정리를 실제로 막아 생성 장벽과 실패 차단을 확인합니다. 패키지 소비·오용 컴파일 검사, 원본 Razor 위치의 분석기 진단 12개, Renderer 수명 실험, 두 브라우저 관찰, 무관찰 운영, 구성 교체, DCMD/NCMD, 브로커 재시작, 정상 종료, 프로세스 강제 종료 Last Will도 검사합니다. JS 테스트의 MQTT 페이로드는 C#과 다른 codec으로 읽습니다.

라이브러리는 `View.Changes`로 변경만 알립니다. `View.ReadAsync(handle => ...)`는 원본 Renderer의 Dispatcher에서 `HtmlRootComponent`를 전달합니다. 문자열이 필요하면 사용자가 `ToHtmlString()`을 호출하고, 다른 자료형이 필요하면 그 자료형을 반환합니다. 살아 있는 렌더 핸들을 보관해 다른 Dispatcher에서 읽지 않고 콜백 안에서 필요한 데이터를 추출합니다.

```razor
<Observer Source="root.View" Read="ReadXml" Context="xml">
    <pre>@xml</pre>
</Observer>

@code {
    private static string ReadXml(HtmlRootComponent root)
        => XDocument.Parse(root.ToHtmlString()).ToString();
}
```

`Observer<TValue>`는 구독과 Dispatcher 접근, 자신의 화면 갱신만 담당합니다. `Read`의 반환값을 `ChildContent`로 전달하므로 문자열·XML 문서·사용자 스냅샷 등 원하는 자료형을 사용할 수 있습니다. 변환·들여쓰기·표시는 소비자가 결정합니다. 관찰자가 없으면 트리의 문자열 변환도 발생하지 않습니다. 예제 앱은 자신의 div/pre 구조에 맞는 들여쓰기를 적용합니다. XML을 출력하는 소비자는 읽기 콜백에서 `XDocument`로 들여쓰기를 적용할 수 있습니다. 관찰은 도메인 상태·수명을 바꾸지 않습니다.

## 패키지 설치

[프리뷰 릴리스](https://github.com/naratteu/backrazor/releases/tag/v0.3.0-preview.1)에서 두 `.nupkg`를 같은 폴더로 다운로드합니다. NuGet.org는 R3 등 외부 의존성을 복원하는 데 사용합니다.

```sh
dotnet add package BackRazor --version 0.3.0-preview.1 --no-restore
dotnet restore --source /absolute/path/to/packages --source https://api.nuget.org/v3/index.json
```

`Root.ApplyAsync(desired)`는 외부에서 받은 새 디바이스 구성을 적용하는 입구입니다. 기존·새 구성의 교집합을 렌더링하고 제거 대상의 비동기 정리를 기다린 뒤 새 구성 전체를 렌더링합니다. 컴포넌트의 자체 상태 갱신은 `InvokeAsync(StateHasChanged)` 등 Blazor의 Dispatcher와 재렌더링 절차를 사용합니다. 구성 교체 장벽은 이 API로 관리하는 루트 항목에 적용하며 임의의 중첩 자식 변경까지 확장되지 않습니다.

## 라이선스

[VibeCoded AI-Slop License v1.0](LICENSE). 의존 라이브러리는 각각의 라이선스를 따릅니다.
