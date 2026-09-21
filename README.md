# One More Card

NPC 1~3명과 플레이하는 텍사스 홀덤 게임입니다. 기본은 4인 테이블입니다.

## 다운로드

- [Windows](https://github.com/haju0423/poker-foundation/releases/download/2026.09.21-holdem/OneMoreCard-Windows.zip)
- [Mac](https://github.com/haju0423/poker-foundation/releases/download/2026.09.21-holdem/OneMoreCard-macOS.zip)

Windows는 압축을 모두 풀고 `One More Card.exe`를 실행합니다. 같은 폴더의 다른 파일도 함께 있어야 합니다.
Mac은 압축을 풀고 `One More Card.app`을 실행합니다.

## 플레이 방법

내 카드 2장과 공용 카드 5장 중 가장 좋은 5장으로 승부합니다.

- 체크·콜·베팅·레이즈·폴드 버튼으로 진행합니다.
- 베팅·레이즈 금액은 이번 베팅 라운드에 낼 총액입니다.
- 쇼다운에서 남은 참가자의 패와 승자를 볼 수 있습니다.
- **다음 판**은 현재 보유 칩으로 이어지며 블라인드를 새로 냅니다. **새 게임**은 현재 판과 칩을 초기화합니다.
- **새 게임 설정**에서 참가 인원과 상대 행동 속도를 바꿀 수 있습니다. 설정을 적용하면 새 게임을 시작합니다.
- **공용 카드 공개 후 잠시 멈추기**를 켜면 플랍·턴·리버 공개 때 멈춥니다. 카드를 보고 **계속**을 누르면 됩니다.
- 결과 화면의 **분배 내역**에서 각 팟을 누가 받았는지 확인하고, 쇼다운에서 공개된 패의 숫자를 비교할 수 있습니다.

## 프로젝트 실행

Unity 6000.3.23f1에서 [프로젝트 폴더](local/PokerCoreLab)를 열고,
[HoldemTable.unity](local/PokerCoreLab/Assets/Poker/Samples/HoldemTable.unity) 장면을 실행합니다.

## 코드 구성

`Assets/Poker` 아래에 기능별로 나뉘어 있습니다.

- `Core`: 카드·족보·베팅·팟 정산·게임 진행
- `Application`: 로컬 테이블과 NPC 행동
- `Presentation`: 문구
- `Runtime`: 게임 화면과 입력 처리
- `Editor`: 장면 구성과 Windows·Mac 빌드
- `Tests`: 포커 규칙과 화면 동작 테스트

폰트 라이선스: [OFL.txt](local/PokerCoreLab/Assets/Poker/Resources/Fonts/OFL.txt)
