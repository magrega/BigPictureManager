# Night Light Logic (Windows CloudStore)

Документ описывает практическую логику работы Night Light в реестре Windows и безопасные правила программной записи.

---

## 1) Где лежат данные

Используются два blob-ключа (`Data`, Binary) в `HKCU`:

- **State** (текущее состояние вкл/выкл и режим):
  - `Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate`
  - fallback (на части систем): `...\Store\DefaultAccount\Cloud\default$windows.data.bluelightreduction.bluelightreductionstate`

- **Settings** (расписание, интервал, сила):
  - `Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.settings\windows.data.bluelightreduction.settings`
  - fallback: `...\Store\DefaultAccount\Cloud\default$windows.data.bluelightreduction.settings`

---

## 2) State: режимы и маркеры

Главный маркер: `state[18]` + длина blob.

- `0x15`, длина `43` -> `manual_on`
- `0x12`, длина `40` -> `schedule_on`
- `0x13`, длина `41` -> `manual_off`
- `0x10`, длина `38` -> `schedule_off`

Важно: для корректной логики нужно учитывать **и маркер, и длину**.

---

## 3) Settings: включено ли планирование

Ключевой признак:

- планирование **включено**: перед `CA 14 0E` стоит `02 01`, типичная длина blob `49`
- планирование **выключено**: `02 01` отсутствует, типичная длина `47`

То есть переключение schedule делается так:

- OFF -> ON: вставить `02 01` перед `CA 14 0E`
- ON -> OFF: удалить `02 01` перед `CA 14 0E`

Дополнительно (из реальных дампов Settings app):

- при ON часто `settings[18] = 0x1B`
- при OFF часто `settings[18] = 0x19`

Если менять только `02 01`, но не синхронизировать служебный marker-байт (в наблюдаемом формате), может появляться рассинхрон UI в "Параметрах".

---

## 4) Интервал расписания

В observed-формате:

`... [02 01]? CA 14 0E <startHour> <startMinute> CA 1E 0E <endHour> <endMinute> ...`

- Start: байты после `CA 14 0E`
- End: байты после `CA 1E 0E`

---

## 5) Сила (warm strength)

В стабильных дампах хорошо работает декодирование по сигнатуре `CF 28`:

- `tempLo` = следующий байт
- `tempHi` = следующий за ним

Формула:

`kelvin = (tempHi * 64) + (tempLo - 128) / 2`

`percentage = 100 - (kelvin - 1200) / (6500 - 1200) * 100`

---

## 6) Безопасные сценарии записи

### 6.1 Отключить Night Light (общий OFF)

- `manual_on (43/0x15)` -> `manual_off (41/0x13)`
- `schedule_on (40/0x12)` -> `schedule_off (38/0x10)`

После сборки нового blob инкрементировать version-байт (обычно первый не `0xFF` в диапазоне `10..14`).

### 6.2 Отключить расписание (UI кнопка "Отключить расписание")

Рекомендуемая ветка:

1. Сначала сделать backup `state + settings`.
2. Прочитать текущий `state`:
   - если `schedule_on` -> снять `02 01` в settings **и** перевести state в `schedule_off`.
   - если `manual_on` -> расписание не трогать, выключить свет вручную (`manual_off`) *или* явно выбрать бизнес-правило проекта.
   - если уже off -> снять `02 01` только в settings.
3. При переключении settings синхронизировать marker-byte формата (`0x1B <-> 0x19` в observed дампах).

### 6.3 Включить расписание

1. Вставить `02 01` перед `CA 14 0E`.
2. Обновить служебный marker/версию.
3. Если текущее время в интервале schedule, система обычно приводит state к `schedule_on`.

---

## 7) Почему "Параметры" могут ломаться

Частые причины:

- изменили только один blob (`state` или `settings`), второй оставили в старой логике;
- сделали структурный сдвиг (длина/блоки), но не синхронизировали служебные байты;
- применили не тот переход (`manual` vs `schedule`) для текущего marker/length;
- отсутствует backup и откат.

---

## 8) Минимальный безопасный чеклист

Перед записью:

1. Прочитать оба blob: `state`, `settings`.
2. Сохранить backup (сырой бинарный snapshot).
3. Определить режим по `state[18] + length`.

После записи:

1. Перечитать оба blob из реестра.
2. Проверить ожидаемые маркеры:
   - state: `0x10/0x12/0x13/0x15` + правильная длина
   - settings: наличие/отсутствие `02 01` перед `CA 14 0E`
3. Если проверка не прошла -> откат из backup.

---

## 9) Что реализовано в `NightLightNew.cs`

Класс `NightLightNew` для .NET Framework 4.7.2:

- `DisableNightLight()`:
  - делает snapshot
  - выполняет безопасную ветку отключения, эквивалентную текущей кнопке "Отключить расписание"

- `RestoreNightLight()`:
  - восстанавливает `state + settings` из snapshot

Snapshot хранится в `%LOCALAPPDATA%\NightLightNew.snapshot.bin`.

