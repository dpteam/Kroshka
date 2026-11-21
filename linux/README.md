# Kroshka для Linux

Клиент и сервер в одном бинарнике. Окно чата открывается в Chromium без адресной
строки, поэтому выглядит как приложение, а не как вкладка браузера.

## Установка

```bash
tar -xzf kroshka-linux-x64.tar.gz
cd kroshka-linux-x64        # или просто в распакованной папке
./install.sh
```

Скрипт спросит, ставить ли службу systemd для сервера, и положит ярлык в меню
приложений. Права root не нужны: всё ставится в `~/.local/share/Kroshka`.

После установки:

```bash
kroshka
```

## Запуск без установки

```bash
./kroshka                    # сервер + окно
./kroshka --headless         # только сервер, без окна
./kroshka --remote 192.168.1.250:6767
```

## Где что лежит

Всё по [XDG Base Directory](https://specifications.freedesktop.org/basedir-spec/latest/),
как принято в Linux, а не в папке программы:

| Что | Где |
|---|---|
| Настройки | `~/.config/Kroshka/client.ini` |
| Данные чата | `~/.local/share/Kroshka/server/data` |
| Логи | `~/.local/state/Kroshka/logs` |
| Кэш браузера | `~/.cache/Kroshka` |
| Ярлык | `~/.local/share/applications/kroshka.desktop` |
| Служба | `~/.config/systemd/user/kroshka.service` |

Это значит, что бэкапить надо `~/.local/share/Kroshka/server/data`, а чистить
кэш можно без страха за переписку.

## Настройки

`~/.config/Kroshka/client.ini`:

```ini
[client]
; local  — этот компьютер запускает сервер сам
; remote — подключение к серверу в сети
mode = local
serverUrl =
localPort = 6767

; auto | chromium | firefox | default
browser =
windowSize = 1200x820
openBrowser = true

[server]
; Отвечать на автопоиск из локальной сети
discoverable = true
```

Формат совпадает с Windows-клиентом, файл можно переносить между машинами.

## Почему Chromium, а не нативное окно

Нативное окно на Linux — это GTK + WebKitGTK. Проблема в том, что soname и API
отличаются между дистрибутивами (`libwebkit2gtk-4.0`, `4.1`, `6.0`), и биндинг
приходится собирать под каждый. Плюс его невозможно протестировать со сборочной
машины на Windows.

Chromium `--app=URL` даёт окно без адресной строки со своим `WM_CLASS`, поэтому
док группирует его под иконкой Kroshka, а не под иконкой браузера. Это работает
на любом дистрибутиве, где есть браузер.

Честная цена решения:

- **Нет иконки в трее.** Linux-трей требует libappindicator и DBus-меню, которые
  ведут себя по-разному в GNOME, KDE и XFCE.
- **Firefox не имеет `--app`**, поэтому там открывается обычное окно. Всё
  остальное работает так же.

Если Chromium не установлен:

```bash
# Debian/Ubuntu
sudo apt install chromium

# Fedora
sudo dnf install chromium

# Arch
sudo pacman -S chromium
```

## Служба systemd

Если этот компьютер — хост для всей семьи, сервер должен работать без окна:

```bash
kroshka --install-service
systemctl --user enable --now kroshka
journalctl --user -u kroshka -f      # логи
```

Служба перезапускается при падении (`Restart=on-failure`) и корректно
останавливается по `SIGTERM`, поэтому последнее сообщение не теряется.

Чтобы сервер жил после выхода из системы (для выделенной машины):

```bash
sudo loginctl enable-linger "$USER"
```

## Требования

- **.NET 10** — не нужен, если сборка self-contained (по умолчанию)
- **libicu** — обязателен для .NET; на минимальных образах его часто нет
- **Chromium** (или Chrome/Edge/Brave) — для окна без адресной строки

```bash
# Debian/Ubuntu
sudo apt install libicu-dev chromium

# Fedora
sudo dnf install libicu chromium

# Arch
sudo pacman -S icu chromium
```

## Архитектуры

| Файл | Для чего |
|---|---|
| `kroshka-linux-x64.tar.gz` | обычные ПК и серверы (amd64) |
| `kroshka-linux-arm64.tar.gz` | Raspberry Pi 4+, ARM-серверы |

## Удаление

```bash
kroshka --uninstall                  # ярлык и службу
rm -rf ~/.local/share/Kroshka        # программу
rm -rf ~/.config/Kroshka             # настройки
rm -rf ~/.local/share/Kroshka/server/data   # переписку (необратимо)
```

Данные не удаляются автоматически: удаление чата не должно стирать историю без
явного согласия.

## Проверка сборки

```powershell
build-linux.cmd          # linux-x64
build-linux.cmd both     # x64 + arm64
build-linux.cmd test     # 46 внутренних проверок клиента
```

Тесты покрывают то, что можно проверить без графики: XDG-пути, разбор адресов,
размеры окна, выбор браузера, чтение и запись конфига, генерацию `.desktop` и
`.service`. Сам GUI проверяется только запуском.
