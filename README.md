# OMEN MAX 16 Control Center 개발 현황 및 로드맵

## 1. 개요
본 문서는 **OMEN MAX 16 Control Center**(C# .NET 10 백엔드 및 WebView2 기반 프론트엔드 제어 인터페이스)의 현재까지의 개발 진척도, 주요 구현 기능, 지원 하드웨어 라인업, 그리고 현재 기술적 한계를 정리한 문서입니다.

본 프로젝트는 HP, Intel, AMD, NVIDIA 및 유관사와 직접적인 관련이 없으며, 해당 상표에 대해 일체의 권리를 주장하지 않습니다.

현재 완성본은 **OMEN MAX 16 (2025 & 2026 전체 라인업)**을 자동 감지(`HardwarePlatformDetector` 및 `resolveHardwareProfile`)하여 동작하도록 확장되었습니다. 주요 실기 검증은 **Intel Core Ultra 9 275HX + NVIDIA RTX 5070 Ti Mobile** 환경에서 수행되었으며, Intel Core Ultra 200HX / 200HX Plus, AMD Ryzen AI 300 / 400 시리즈 및 RTX 5060 ~ 5090 GPU 조합에 대한 전력 한계치·프리셋·동적 전력 배분(TPP) 프로파일이 모두 통합되어 있습니다.

---

## 2. 개발 진척도

| 영역 | 상태 | 주요 진척 내용 |
| :--- | :---: | :--- |
| **코어 UI 및 테마 시스템** | 완료 | 파스텔 세라믹(라이트) 및 무광 다크 테마 전환, 스포트라이트 카드(`GlassCard`), RGB 상·하단 바, 부팅/렌더링 에러 가드(`#boot-error`, `ErrorBoundary`) 구축 |
| **실시간 모니터링 대시보드** | 완료 | CPU/GPU 온도·사용량·전력·클럭, CPU 전압/FPS, 듀얼 팬 RPM/% 및 애니메이션, 배터리 잔량/전력/충전 상태·잠금 토글 표시 |
| **센서 텔레메트리 정확도 고도화** | 완료 | Windows 커널 PDH(`APERF/MPERF`) 기반 CPU 실측 유효 클럭 계산(Arrow Lake `275HX`의 `5.15 GHz` 레지스터 고정 우회), 활성 코어 가중 평균 온도 및 C-State 전압 딥 보정 적용 |
| **전력·주사율 및 HUD 제어** | 완료 | `ECO` / `BALANCED` / `MAX` 전력 프로필, 디스플레이 주사율(`240Hz` 등) 토글, 게임 오버레이 HUD(`FormGameHud`) 및 미디어 HUD(`FormMediaHud`) 연동 |
| **CPU / GPU 팝업 안정화** | 완료 | `CPU 전력` 및 `GPU` 팝업 클릭 차단·이벤트 간섭 이슈 해결 및 `Popover` 기반 안정화 완료 |
| **팬 모드 및 팬 커브 편집기** | 완료 | `AUTO` / `QUIET` / `COOL` 모드 전환 및 SVG 기반 팬 커브 편집기(`#curve-modal`, 포인트 추가·드래그·우클릭 삭제, CPU/GPU 탭 전환 및 저장) 구현 |
| **멀티 CPU/GPU 라인업 지원** | 완료 | `HardwarePlatformDetector` + `resolveHardwareProfile` 연동으로 **Intel 200HX/Plus**, **AMD Ryzen AI 300/400**, **RTX 5060~5090**, **TPP 250~300W** 자동 판별 및 동적 적용 완료 |

---

## 3. 현재 구현된 주요 기능

### 3.1. 실시간 텔레메트리 및 상태 인디케이터
- **CPU 모니터링 (`HardwareCombinedMonitoringService`)**:
  - 사용률(`%`), 소비 전력(`W` 및 유효 상한 대비 비율), 온도(`°C`), 전압(`V`), FPS(`fps`, `PresentMon`/`RTSS` 연동) 표시 및 부드러운 보간(`useSmooth`) 적용.
  - **Windows 커널 PDH 유효 클럭(`APERF/MPERF`)**: Intel Arrow Lake(`275HX` 등)에서 `IA32_PERF_STATUS` MSR 배수가 `5.15 GHz`로 고정(래치)되는 현상을 우회하여 실제 동작 유효 클럭(`GHz`)을 정확하게 산출.
  - **플랫폼별 온도·전압 정제**: Intel 환경에서는 전체 코어 평균에 활성 부하 코어 가중치(최대 `1.5배`)를 부여해 단일 코어 스파이크를 완화하고, AMD 환경에서는 `Tctl/Tdie` 및 Ryzen Master/RyzenAdj 브릿지 텔레메트리를 우선 반영.
- **GPU 모니터링**:
  - 모델명 자동 축약 표기(`formatShortGpuName`), 활성/미검출/비활성 상태 처리, 온도(`°C`), 클럭(`GHz`), 사용률(`%`), 소비 전력(`W`) 표시 및 `BOOST` 배지/고성능 프로필 강조 효과 적용.
- **통합 상태 인디케이터(`StatusDot`)**: CPU 온도, GPU 온도, CPU 전압 임계치를 종합해 `정상` / `주의` / `경고` / `위험` 4단계 상태 색상 및 점멸 주기로 표시.
- **배터리 제어**: 배터리 잔량(`%`), 충·방전 전력(`W`), 충전 상태 펄스 효과 및 배터리 잠금(`TOGGLE_BATTERY_LOCK`) 토글 제공.

### 3.2. 플랫폼 자동 판별 및 전력 제어 팝업 (`CPU 전력` / `GPU`)
- **하드웨어 프로파일 자동 판별 (`HardwarePlatformDetector` & `resolveHardwareProfile`)**:
  - 시스템의 CPU 제조사·세대 및 GPU 등급을 자동 감지하여 슬라이더 하한·상한, 프리셋 단계, cTGP/Dynamic Boost 수치, TPP 총 전력 예산을 동적으로 구성.
- **CPU 전력 제어 팝업 (`IntelCpuTuningService` / `AmdCpuTuningService` 연동)**:
  - **Intel 플랫폼**: `PL1`(지속 전력, `20~140W` / 2026 Plus 최대 `150W`), `PL2`(부스트 전력, 최대 `160W` / `290HX` 최대 `171W`), `Tau`(부스트 유지 시간, `1~56초`) 슬라이더 제어.
  - **AMD 플랫폼**: `STAPM / PL1`(`15~60/65W`), `PPT Fast / PL2`(`15~75/80W`), `STAPM Tau`(`1~56초`) 라벨 및 범위 자동 전환.
  - 아키텍처별 맞춤 5단계 프리셋(`저전력`, `정숙 게임`, `균형 게임`, `고성능 게임`, `최대 성능`) 지원.
  - **TPP 게이밍 동적 배분 (`250W` / `265W` / `300W`)**: GPU 고부하 시 동시 부하 CPU 상한(`Intel 55~115/130W`, `AMD 25~65/70W`)과 GPU 여유 전력을 자동 균형 유지하며 낙관적 UI 업데이트 적용.
- **GPU 전력 제어 팝업 (`HpOmenGpuPowerController`)**:
  - 모델별 기본 TGP, `cTGP 확장`, `Dynamic Boost (PPAB +15W / +25W)` 토글 및 상한 전력(`115W` / `140W` / `175W`) 자동 매칭.
  - GPU 클럭 상한(`MHz`) 프리셋 및 슬라이더(`1200~2800MHz`), GPU 끄기(`SET_GPU_MODE`, `-1`) 및 적용(`SET_GPU_CLOCK_CAP`) 지원.
  - 모델별 최소~최대 TGP에 연동되는 동적 클럭-전력 추정 곡선(`buildCapTable` / `estWatts`) 내장.

### 3.3. 팬 제어 및 커브 편집기
- **팬 상태 및 모드**: `CPU FAN`, `GPU FAN`의 RPM 및 `%` 표시, 팬 회전 속도 연동 애니메이션, `QUIET`/`COOL` 모드 시 동기화(`SYNC`) 배지 표시.
- **팬 커브 편집기(`#curve-modal`)**: `35°C~100°C` / `0%~100%` 좌표계에서 클릭 추가, 드래그 이동, 우클릭 포인트 삭제, `CPU 팬 커브` / `GPU 팬 커브` 탭 전환, `QUIET` / `COOL` 커브 저장 메시지(`SAVE_QUIET_CURVE`, `SAVE_CUSTOM_CURVE`) 전송 지원.

### 3.4. 오버레이 HUD 및 백엔드 양방향 통신
- **게임 및 미디어 HUD**: 상단 퀵 버튼을 통해 게임 오버레이 HUD(`TOGGLE_HUD`) 및 미디어 컨트롤 HUD(`TOGGLE_MEDIA_HUD`) 즉시 전환.
- **WebView2 브릿지 동기화**: C# 호스트(`FormMain.cs`)와 프론트엔드(`Form.html`) 간 실시간 텔레메트리(`d.cpu`, `d.gpu`, `d.fan`, `d.battery`, `d.ui`) 및 제어 커맨드(`SAVE_CPU_POWER`, `SET_GPU_POWER`, `SET_GPU_CLOCK_CAP`, `SET_FAN_MODE`, `SAVE_QUIET_CURVE`, `SAVE_CUSTOM_CURVE` 등) 양방향 연동.

---

## 4. 지원 하드웨어 라인업 및 플랫폼 사양 (구현 완료)

백엔드(`HardwarePlatformDetector`)와 프론트엔드(`resolveHardwareProfile`)가 아래 라인업을 자동 인식하여 제어 파라미터를 구성합니다.

1. **CPU 지원 라인업**
   - **Intel Core Ultra 200HX (2025)**: Ultra 7 255HX, Ultra 9 275HX 등 (`PL1` 최대 `140W` / `PL2` 최대 `160W`, TPP CPU 상한 `55~115W`)
   - **Intel Core Ultra 200HX Plus (2026)**: Ultra 7 270HX Plus, Ultra 9 290HX Plus 등 (`PL1` 최대 `150W` / `PL2` 최대 `171W`, TPP CPU 상한 `55~130W`)
   - **AMD Ryzen AI 300 / 400 시리즈**: Ryzen AI 7 350 / 450 (`STAPM` 최대 `60W` / `PPT` 최대 `75W`), Ryzen AI 9 HX 375 / HX 475 (`STAPM` 최대 `65W` / `PPT` 최대 `80W`)

2. **GPU 지원 라인업 (`NVIDIA GeForce RTX 50 Laptop GPU`)**
   - **RTX 5060 / RTX 5070 Laptop GPU**: 기본 `85W` → cTGP `100W` + Dynamic Boost `15W` = **최대 `115W`**
   - **RTX 5070 Ti Laptop GPU**: 기본 `100W` → cTGP `115W` + Dynamic Boost `25W` = **최대 `140W`**
   - **RTX 5080 / RTX 5090 Laptop GPU**: 기본 `130W` → cTGP `150W` + Dynamic Boost `25W` = **최대 `175W`**

3. **통합 플랫폼 전력 예산 (TPP)**
   - 모델 연식 및 GPU 등급 조합에 따라 **2025 라인업 `250W`**, **2026 라인업 `250W / 265W / 300W`** 총 플랫폼 전력 예산이 자동 산출되며, CPU와 GPU 간 동적 전력 배분 슬라이더 범위에 즉시 반영됩니다.

---

## 5. 현재 한계점 및 향후 고도화 과제

1. **파생 하드웨어 모델의 실기 미세 튜닝 필요**
   - `Intel Core Ultra 9 275HX + RTX 5070 Ti` 모델에서는 실기 검증이 완료되었으나, 그 외 파생 모델(2026 Plus 라인업, AMD Ryzen AI 모델, RTX 5060/5080/5090 구성)은 하드웨어 스펙 및 프로파일 테이블 기반으로 구현되어 있어 실기 테스트를 통한 세부 전력·팬 커브 보정이 추가로 필요합니다.
2. **커널 드라이버 및 보안 정책 의존성**
   - Intel MSR 전력 제한(`WinRing0x64`), AMD SMU 튜닝(`RyzenAdj` / 브릿지), HP OMEN WMI BIOS 및 NVIDIA NVAPI 제어를 위해 관리자 권한 실행이 필요하며, Windows 메모리 무결성(HVCI) 등 보안 설정에 따라 일부 저수준 레지스터 접근이 제한될 수 있습니다.
3. **단일 파일(`Form.html`) 구조의 모듈화 여지**
   - 별도 빌드 파이프라인 없이 WebView2에서 즉시 구동되도록 단일 HTML 파일에 스타일·React 컴포넌트·팬 커브 SVG 스크립트가 통합되어 있어, 향후 게임별 자동 프로필 전환 등의 대형 기능 추가 시 모듈 분리를 고려할 수 있습니다.