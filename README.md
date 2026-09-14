# Five Card Draw

## 실행

Unity **6000.3.23f1**에서 `local/PokerCoreLab` 폴더를 열고,
`Assets/Poker/Samples/PracticeTable.unity` 장면에서 Play를 누릅니다.

## 조작

- 베팅 버튼으로 체크·콜·레이즈·폴드를 선택합니다.
- 카드 교환 때 바꿀 카드를 누르고 확정합니다. 0장을 선택하면 패를 유지합니다.
- 베팅·레이즈 입력값은 이번 베팅 라운드의 총액입니다.

현재는 2인 로컬 플레이입니다. 블라인드는 1/2이며 자리와 행동 순서는 고정입니다.
온라인 멀티플레이와 LLM은 포함하지 않습니다.

## 프로젝트

소스·장면·설정·테스트는 `local/PokerCoreLab`에 있습니다.
테스트는 Unity의 **Window → General → Test Runner**에서 실행합니다.

폰트 라이선스: [OFL.txt](local/PokerCoreLab/Assets/Poker/Resources/Fonts/OFL.txt)
