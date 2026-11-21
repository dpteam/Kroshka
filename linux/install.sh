#!/usr/bin/env bash
# ============================================================================
#  Kroshka — установка на Linux
#
#  Распакуйте архив и запустите:
#
#      ./install.sh
#
#  Что делает:
#    * копирует программу в ~/.local/share/Kroshka (или /opt при sudo)
#    * ставит ярлык в меню приложений
#    * по желанию — службу systemd --user для сервера
#    * НЕ трогает права root, если вы их не дали сами
#
#  Удалить:  kroshka --uninstall && rm -rf ~/.local/share/Kroshka
# ============================================================================

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---------------------------------------------------------------- colours

if [ -t 1 ]; then
    BOLD=$'\033[1m'; DIM=$'\033[2m'; RED=$'\033[31m'
    GREEN=$'\033[32m'; BLUE=$'\033[34m'; RESET=$'\033[0m'
else
    BOLD=""; DIM=""; RED=""; GREEN=""; BLUE=""; RESET=""
fi

say()  { printf '%s\n' "$*"; }
ok()   { printf '  %s✓%s %s\n' "$GREEN" "$RESET" "$*"; }
warn() { printf '  %s!%s %s\n' "$RED" "$RESET" "$*"; }
step() { printf '\n%s%s%s\n' "$BOLD" "$*" "$RESET"; }

# ------------------------------------------------------------------- detect

if [ ! -f "$ROOT/kroshka" ]; then
    warn "рядом со скриптом нет файла kroshka"
    say  "  Распакуйте архив целиком и запустите install.sh из его папки."
    exit 1
fi

ARCH="$(uname -m)"
case "$ARCH" in
    x86_64|amd64)  RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *)
        warn "неизвестная архитектура: $ARCH"
        say  "  Соберите сами: dotnet publish -r linux-x64"
        exit 1
        ;;
esac

# A system-wide install needs root; everything else is happier per-user.
if [ "$(id -u)" -eq 0 ]; then
    TARGET="/opt/Kroshka"
    SCOPE="системная установка"
else
    TARGET="${XDG_DATA_HOME:-$HOME/.local/share}/Kroshka"
    SCOPE="для текущего пользователя"
fi

step "Kroshka — установка ($SCOPE)"
say  "  архитектура : $ARCH ($RID)"
say  "  каталог     : $TARGET"

# -------------------------------------------------------------- dependencies

step "Проверка зависимостей"

MISSING=0

if ! command -v dotnet >/dev/null 2>&1; then
    # A self-contained build carries its own runtime; only the framework-dependent
    # one needs dotnet on the machine.
    if [ -f "$ROOT/libhostfxr.so" ] || [ -f "$ROOT/dotnet" ]; then
        ok "рантайм в комплекте"
    else
        warn "dotnet не найден, а сборка не self-contained"
        say  "  Установите .NET 10: https://dotnet.microsoft.com/download"
        MISSING=1
    fi
else
    ok "dotnet $(dotnet --version 2>/dev/null || echo '?')"
fi

# The UI needs a browser; Chromium gives a real app window, Firefox a normal one.
BROWSER=""
for candidate in chromium chromium-browser google-chrome google-chrome-stable brave-browser microsoft-edge firefox; do
    if command -v "$candidate" >/dev/null 2>&1; then BROWSER="$candidate"; break; fi
done

if [ -n "$BROWSER" ]; then
    ok "браузер: $BROWSER"
else
    warn "браузер не найден"
    say  "  Поставьте Chromium для окна без адресной строки:"
    say  "    Debian/Ubuntu: sudo apt install chromium"
    say  "    Fedora:        sudo dnf install chromium"
    say  "    Arch:          sudo pacman -S chromium"
fi

# libicu is the classic missing piece for .NET on a minimal Linux.
if [ -f /etc/os-release ]; then
    if ! ldconfig -p 2>/dev/null | grep -q libicu; then
        warn "libicu не найден — .NET без него не стартует"
        say  "    Debian/Ubuntu: sudo apt install libicu-dev"
        say  "    Fedora:        sudo dnf install libicu"
        say  "    Arch:          sudo pacman -S icu"
    else
        ok "libicu"
    fi
fi

if [ "$MISSING" -eq 1 ]; then
    warn "установка прервана: не хватает зависимостей"
    exit 1
fi

# ------------------------------------------------------------------ install

step "Копирование файлов"

mkdir -p "$TARGET"

# Preserve a config.ini the user may already have edited.
if [ -f "$TARGET/config.ini" ]; then
    cp -f "$TARGET/config.ini" "$TARGET/config.ini.keep"
fi

cp -a "$ROOT"/. "$TARGET"/

if [ -f "$TARGET/config.ini.keep" ]; then
    mv -f "$TARGET/config.ini.keep" "$TARGET/config.ini"
    ok "сохранён прежний config.ini"
fi

chmod +x "$TARGET/kroshka" 2>/dev/null || true

# A system-wide install should be readable but not writable by everyone.
if [ "$(id -u)" -eq 0 ]; then
    chmod -R a+rX "$TARGET"
fi

ok "файлы на месте"

# --------------------------------------------------------------- integration

step "Ярлык в меню"

if "$TARGET/kroshka" --install-desktop >/dev/null 2>&1; then
    ok "ярлык создан"
else
    warn "не удалось создать ярлык (не критично)"
fi

# ------------------------------------------------------------------- service

step "Служба сервера"

say "  Сервер может работать без окна и подниматься при входе в систему."
say "  Нужно, только если этот компьютер — хост для всей семьи."

if [ -t 0 ]; then
    printf '  Установить службу systemd? [y/N] '
    read -r ANSWER || ANSWER="n"
else
    ANSWER="n"
fi

case "$ANSWER" in
    [yYдД]*)
        if "$TARGET/kroshka" --install-service >/dev/null 2>&1; then
            ok "служба установлена"
            say ""
            say "  Включить и запустить:"
            say "    ${BLUE}systemctl --user enable --now kroshka${RESET}"
            say ""
            say "  Логи:"
            say "    ${BLUE}journalctl --user -u kroshka -f${RESET}"
        else
            warn "не удалось установить службу"
        fi
        ;;
    *)
        say "  пропущено"
        ;;
esac

# ------------------------------------------------------------------- summary

step "Готово"

say "  Запустить:"
if [ "$(id -u)" -eq 0 ]; then
    say "    ${BLUE}$TARGET/kroshka${RESET}"
else
    say "    ${BLUE}kroshka${RESET}  ${DIM}(или из меню приложений)${RESET}"
fi

say ""
say "  Настройки : ${DIM}${XDG_CONFIG_HOME:-$HOME/.config}/Kroshka/client.ini${RESET}"
say "  Данные    : ${DIM}${XDG_DATA_HOME:-$HOME/.local/share}/Kroshka${RESET}"
say "  Логи      : ${DIM}${XDG_STATE_HOME:-$HOME/.local/state}/Kroshka/logs${RESET}"
say ""
say "  Удалить:"
say "    ${BLUE}$TARGET/kroshka --uninstall${RESET}"
say "    ${BLUE}rm -rf $TARGET${RESET}"
say ""

# A per-user install under ~/.local/bin is on PATH on most distributions, but a
# short wrapper makes `kroshka` work even where it is not.
if [ "$(id -u)" -ne 0 ]; then
    BIN_DIR="$HOME/.local/bin"
    if [ -d "$BIN_DIR" ]; then
        ln -sf "$TARGET/kroshka" "$BIN_DIR/kroshka" 2>/dev/null && \
            say "  Команда ${BLUE}kroshka${RESET} доступна из терминала."
    else
        say "  ${DIM}Подсказка: добавьте $TARGET в PATH, чтобы запускать командой kroshka.${RESET}"
    fi
fi
