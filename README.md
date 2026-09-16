# One More Card

텍사스 홀덤 기본형입니다. 기본 실행은 플레이어 1명과 NPC 3명의 로컬 4인 테이블입니다.
현재는 기본 포커를 확인하기 위한 개발 버전이며, AI 대화·카드 조작·의심도·온라인 멀티플레이는 포함하지 않습니다.

## 실행파일

- [Windows 64비트 다운로드](https://github.com/haju0423/poker-foundation/releases/download/2026.09.16-holdem-preview/OneMoreCard-Windows.zip)
- [Mac 다운로드](https://github.com/haju0423/poker-foundation/releases/download/2026.09.16-holdem-preview/OneMoreCard-macOS.zip)
- [개발 버전 안내](https://github.com/haju0423/poker-foundation/releases/tag/2026.09.16-holdem-preview)

Windows는 압축을 모두 푼 뒤 `One More Card.exe`를 실행합니다. 같은 폴더의 `One More Card_Data`와 실행에 필요한 파일도 함께 있어야 합니다.
Mac은 압축을 푼 뒤 `One More Card.app`을 실행합니다. Apple Silicon과 Intel 빌드를 함께 포함합니다.
두 버전 모두 창모드로 시작합니다.

사용자의 실제 마우스·터치패드로 실행파일을 조작하는 검증은 아직 남아 있습니다. Windows는 실제 PC에서의 실행도 아직 확인하지 않았습니다.

## 조작

- 베팅 버튼으로 체크·콜·베팅·레이즈·폴드를 선택합니다.
- 내 카드 2장과 공용 카드 5장 중 가장 좋은 5장으로 승부합니다. 카드 교환은 없습니다.
- 프리플랍 → 플랍 → 턴 → 리버 순서로 베팅합니다.
- 베팅·레이즈 입력값은 이번 베팅 라운드의 총액입니다.
- 쇼다운에서는 남은 참가자의 패와 족보를 공개합니다. 폴드로 끝난 판은 공개하지 않습니다.
- 올인 금액이 다르면 메인 팟과 사이드 팟을 나눠 정산합니다. 상대가 따라 내지 않은 칩은 반환합니다.
- **다음 판**은 칩을 유지하고 버튼 위치를 바꿉니다. **처음부터**만 칩을 초기화합니다.
- 칩이 없는 참가자는 다음 판부터 빠집니다. 플레이어가 탈락해도 남은 NPC들의 경기를 관전할 수 있습니다.
- 동률로 나눈 뒤 칩이 남으면 지급 대기 화면이 나옵니다. 버튼 다음 자리부터 지급하는 임시 규칙을 **그 판에만 명시적으로 적용**할 수 있습니다. 팀의 최종 규칙으로 저장하지 않습니다.

## 설정

`Assets/Poker/Samples/HoldemSettings.asset`에서 참가 인원(2–4명)·시작 칩·블라인드·상대 행동 간격을 설정합니다.
현재 테스트 설정은 시작 칩 100, 블라인드 1/2, 노리밋입니다. 베팅 세부 규칙과 경제 설정은 팀 확정 전입니다.
버튼은 매 판 다음 생존자로 이동하는 [forward-moving 방식](https://www.pokerstars.com/help/articles/fwd-moving-button/)입니다. 2명일 때 버튼이 SB입니다. 탈락 후 3명에서 2명으로 바뀌는 상황에 따라 같은 사람이 BB를 연속 맡을 수 있으며, TDA의 dead-button 방식과는 다릅니다.
앤티, 중도 참가·리바이, 온라인 접속은 지원하지 않습니다.

NPC는 자기 패와 공개 카드로만 판단하는 로컬 규칙 기반 상대입니다.
화면은 자기 좌석에 연결된 입력 창구만 사용하며 덱과 상대의 비공개 패를 받지 않습니다.

## 확인한 범위

- 2026-09-16 전체 자동 테스트 EditMode 819개, PlayMode 50개 통과. 기존 드로우·2인 홀덤 회귀 테스트를 포함합니다.
- 카드 배분·족보·사이드 팟·정산 대기·탈락 후 진행·칩 유지·잘못된 명령 거부를 자동 검사했습니다.
- 960×640 작은 창과 저장된 4인 장면의 포인터 hit test·누름/뗌·콜백을 검사했습니다. OS에서 들어오는 실제 마우스·터치패드 입력을 대신 검증한 것은 아닙니다.
- 자동 검사와 실제 조작·플레이 평가는 별개입니다. UI는 기능 확인용이며, 완성된 게임의 재미나 최종 디자인을 검증한 상태는 아닙니다.

## 프로젝트

Unity **6000.3.23f1**에서 `local/PokerCoreLab` 폴더를 열고,
`Assets/Poker/Samples/HoldemTable.unity` 장면에서 Play를 누릅니다.

소스·장면·설정·테스트는 `local/PokerCoreLab`에 있습니다.
핵심 규칙은 `Assets/Poker/Core/Holdem*.cs`, 로컬 진행은 `Application/Holdem*.cs`, 화면은 `Runtime/Holdem*.cs`에 있습니다.
공용 카드와 홀 카드는 별도로 관리하고, 기존 카드·5장 족보 판정·칩 원장·베팅·팟 정산을 재사용합니다.
테스트는 Unity의 **Window → General → Test Runner**에서 실행합니다.
장면 생성·빌드 진입점은 `Poker.Editor.HoldemSceneBuilder`입니다.

기존 드로우 장면은 `PracticeTable.unity`로 보존했습니다. [이전 Five Card Draw 실행파일](https://github.com/haju0423/poker-foundation/releases/tag/2026.09.14)도 별도로 남겨두었습니다.

폰트 라이선스: [OFL.txt](local/PokerCoreLab/Assets/Poker/Resources/Fonts/OFL.txt)
