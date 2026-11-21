# Android-клиент Kroshka — план

Пока это документ, а не код. Здесь записано, **почему** именно так и что уже готово
на стороне сервера, чтобы мобильный клиент не потребовал переделок.

## Главное решение: тот же `web/`

Android-приложение — это `WebView`, который открывает интерфейс из `web/`. Ровно так
же сделан десктопный клиент и так же устроен Android-клиент в AutoZapret.

Причина простая: у нас один интерфейс, один набор багов и одна правка. Второй UI на
Kotlin/Compose означал бы второй источник правды для чата, реакций, голосовых и
поиска — и вечное расхождение между платформами.

```
android/
├─ Kroshka.Android/
│  ├─ Kroshka.Android.csproj      net10.0-android
│  ├─ MainActivity.cs             WebView + мост
│  ├─ Bridge.cs                   [JavascriptInterface] для window.kroshkaAndroid
│  └─ Resources/                  иконки, стили, splash
├─ build.cmd
└─ README.md
```

## Что уже готово на сервере

| Нужно клиенту | Готово |
|---|---|
| Вход по нику, запоминание устройства | ✅ `/api/login` возвращает токен |
| REST для истории и команд | ✅ `/api/*` |
| Живые сообщения | ✅ WebSocket `/ws?t=<token>` |
| Файлы и голосовые | ✅ `/api/upload`, `/api/blob/<id>` |
| Токен в query, а не в заголовке | ✅ уже поддержан для `<img>`/`<audio>` |
| Один интерфейс на всех | ✅ `web/` без сборки, без npm |

Ничего дописывать в протокол не нужно. Это и было целью: сервер проектировался
так, чтобы Android встал сбоку, а не потребовал редизайна.

## Тулчейн

```powershell
dotnet workload install android
dotnet build -t:InstallAndroidDependencies -f net10.0-android `
    -p:AndroidSdkDirectory="C:\Android\sdk" -p:AcceptAndroidSDKLicenses=True
```

Дальше — `android\build.cmd`, он сам найдёт `ANDROID_HOME` или `C:\Android\sdk`.

## Мост

Веб-страница уже умеет звать нативные функции через `window.desktopBridge`:

```js
window.desktopBridge.setBadge(count);      // десктоп: значок в трее
window.desktopBridge.notify(title, body);  // десктоп: тост Windows
```

На Android тот же вызов подменяется на `window.kroshkaAndroid`, и HTML **не форкается**:

```csharp
[Android.Runtime.Register("kroshkaAndroid")]
public sealed class Bridge : Java.Lang.Object
{
    [Android.Runtime.Register("setBadge", "(I)V", "GetSetBadgeHandler")]
    public void SetBadge(int count) { /* badge на иконке лончера */ }

    [Android.Runtime.Register("notify", "(Ljava/lang/String;Ljava/lang/String;)V", "GetNotifyHandler")]
    public void Notify(string title, string body) { /* NotificationCompat */ }
}
```

## Что нужно решить при реализации

1. **Откуда брать `web/`.** Либо копировать в APK как assets и грузить через
   `WebViewAssetLoader` (работает офлайн, но требует пересборки APK при правке UI),
   либо всегда грузить с сервера (обновление мгновенное, но без сети — белый экран).
   Скорее всего: assets по умолчанию + обновление с сервера, если он ответил.

2. **Голосовые.** `MediaRecorder` в WebView требует `RECORD_AUDIO` и
   `onPermissionRequest`. Проверить на Android 13+, где разрешения стали строже.

3. **Хранение токена.** `EncryptedSharedPreferences`, не обычные настройки.

4. **Фон.** WebSocket в фоне Android убивает. Нужен либо foreground service,
   либо push. Для семейного чата проще foreground service с постоянным уведомлением.

5. **Cleartext HTTP.** LAN-адреса по `http://` — нужен `usesCleartextTraffic=true`
   либо `network_security_config` с исключением для локальных подсетей.

## Порядок работ

1. WebView + вход по нику + список комнат.
2. Сообщения: отправка, приём по WebSocket, история с пагинацией.
3. Вложения и голосовые.
4. Уведомления и badge.
5. Foreground service для фона.
6. Сборка APK в CI (отдельный job, чтобы не тормозить десктопную сборку).
