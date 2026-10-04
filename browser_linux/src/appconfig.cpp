#include "appconfig.h"

#include <QDir>
#include <QFileInfo>
#include <QSet>

AppConfig AppConfig::load(const QString &configPath)
{
    AppConfig fallback;
    QFileInfo info(configPath);
    QDir().mkpath(info.absolutePath());

    QFile file(configPath);
    if (!file.exists()) {
        if (file.open(QIODevice::WriteOnly | QIODevice::Truncate)) {
            QJsonObject object;
            object.insert(QStringLiteral("url"), fallback.url);
            object.insert(QStringLiteral("defaultUrl"), fallback.defaultUrl);
            object.insert(QStringLiteral("defaultPort"), fallback.defaultPort);
            object.insert(QStringLiteral("localIndexPath"), fallback.localIndexPath);
            object.insert(QStringLiteral("kiosk"), fallback.kiosk);
            object.insert(QStringLiteral("title"), fallback.title);
            object.insert(QStringLiteral("icon"), fallback.icon);
            object.insert(QStringLiteral("allowDevTools"), fallback.allowDevTools);
            file.write(QJsonDocument(object).toJson(QJsonDocument::Indented));
            file.close();
        }
        return fallback;
    }

    if (!file.open(QIODevice::ReadOnly)) {
        return fallback;
    }

    const auto data = file.readAll();
    file.close();

    const auto doc = QJsonDocument::fromJson(data);
    if (!doc.isObject()) {
        return fallback;
    }

    const auto object = doc.object();
    AppConfig cfg;
    cfg.url = object.value(QStringLiteral("url")).toString();
    cfg.defaultUrl = object.value(QStringLiteral("defaultUrl")).toString(fallback.defaultUrl);
    cfg.defaultPort = object.value(QStringLiteral("defaultPort")).toInt(fallback.defaultPort);
    cfg.localIndexPath = object.value(QStringLiteral("localIndexPath")).toString(fallback.localIndexPath);
    cfg.kiosk = object.value(QStringLiteral("kiosk")).toBool(fallback.kiosk);
    cfg.title = object.value(QStringLiteral("title")).toString();
    cfg.icon = object.value(QStringLiteral("icon")).toString();
    cfg.allowDevTools = object.value(QStringLiteral("allowDevTools")).toBool(fallback.allowDevTools);
    return cfg;
}

QString AppConfig::effectiveTitle() const
{
    return title.trimmed().isEmpty() ? hardcodedTitle : title.trimmed();
}

QString AppConfig::resolveStartupTarget(const QString &configPath) const
{
    const QString configDirectory = QFileInfo(configPath).absolutePath();
    QString target;

    if (tryResolveConfigTarget(url, configDirectory, target)) {
        return target;
    }

    if (tryResolveConfigTarget(localIndexPath, configDirectory, target, true)) {
        return target;
    }

    return buildDefaultUrl();
}

QString AppConfig::resolveCustomIconPath(const QString &configPath) const
{
    if (icon.trimmed().isEmpty()) {
        return {};
    }

    const QString configDirectory = QFileInfo(configPath).absolutePath();
    const QString candidate = QFileInfo(QDir(configDirectory), icon.trimmed()).absoluteFilePath();
    return QFileInfo::exists(candidate) ? candidate : QString{};
}

QString AppConfig::resolveFallbackIconPath() const
{
    const QString candidate = QFileInfo(QDir(QCoreApplication::applicationDirPath()), hardcodedIconRelativePath).absoluteFilePath();
    return QFileInfo::exists(candidate) ? candidate : QString{};
}

bool AppConfig::tryResolveConfigTarget(const QString &rawValue,
                                       const QString &configDirectory,
                                       QString &target,
                                       bool requireExistingFile)
{
    target.clear();
    const QString trimmed = rawValue.trimmed();
    if (trimmed.isEmpty()) {
        return false;
    }

    const QUrl asUrl(trimmed);
    if (asUrl.isValid() && asUrl.isRelative() == false && !asUrl.scheme().isEmpty()) {
        if (asUrl.scheme() == QStringLiteral("file")) {
            if (requireExistingFile && !QFileInfo::exists(asUrl.toLocalFile())) {
                return false;
            }
        }
        target = asUrl.toString();
        return true;
    }

    const QString candidatePath = QFileInfo(QDir(configDirectory), trimmed).absoluteFilePath();
    if (QFileInfo::exists(candidatePath)) {
        target = QUrl::fromLocalFile(candidatePath).toString();
        return true;
    }

    if (requireExistingFile) {
        return false;
    }

    QString normalizedWebUrl;
    if (tryNormalizeWebUrl(trimmed, normalizedWebUrl)) {
        target = normalizedWebUrl;
        return true;
    }

    return false;
}

QString AppConfig::buildDefaultUrl() const
{
    QString normalizedBaseUrl;
    if (!tryNormalizeWebUrl(defaultUrl.trimmed().isEmpty() ? QStringLiteral("http://127.0.0.1") : defaultUrl.trimmed(), normalizedBaseUrl)) {
        normalizedBaseUrl = QStringLiteral("http://127.0.0.1");
    }

    QUrl urlValue(normalizedBaseUrl);
    if (defaultPort > 0) {
        urlValue.setPort(defaultPort);
    }
    return urlValue.toString();
}

bool AppConfig::tryNormalizeWebUrl(const QString &rawValue, QString &normalizedUrl)
{
    normalizedUrl.clear();
    const QString value = rawValue.trimmed();
    if (value.isEmpty()) {
        return false;
    }

    const QUrl absoluteUrl(value);
    if (absoluteUrl.isValid() && !absoluteUrl.isRelative() &&
        (absoluteUrl.scheme() == QStringLiteral("http") || absoluteUrl.scheme() == QStringLiteral("https"))) {
        normalizedUrl = absoluteUrl.toString();
        return true;
    }

    if (!looksLikeHostOrIp(value)) {
        return false;
    }

    const QUrl withScheme(QStringLiteral("http://") + value);
    if (withScheme.isValid() && !withScheme.isRelative()) {
        normalizedUrl = withScheme.toString();
        return true;
    }

    return false;
}

bool AppConfig::looksLikeHostOrIp(const QString &value)
{
    if (value.startsWith(QStringLiteral("http://"), Qt::CaseInsensitive) ||
        value.startsWith(QStringLiteral("https://"), Qt::CaseInsensitive) ||
        value.startsWith(QStringLiteral("file://"), Qt::CaseInsensitive)) {
        return true;
    }

    if (value.contains(u'\\')) {
        return false;
    }

    if (!value.contains(u'/') && hasLikelyLocalFileExtension(value)) {
        return false;
    }

    if (value.startsWith(QStringLiteral("localhost"), Qt::CaseInsensitive)) {
        return true;
    }

    if (!value.isEmpty() && value.front().isDigit()) {
        return true;
    }

    if (value.startsWith(u'[')) {
        return true;
    }

    const QString hostPortPath = value.section(u'/', 0, 0);
    const QString hostOnly = hostPortPath.section(u':', 0, 0);
    return hostOnly.contains(u'.') && !hostOnly.contains(u' ');
}

bool AppConfig::hasLikelyLocalFileExtension(const QString &value)
{
    const QString suffix = QFileInfo(value).suffix().toLower();
    static const QSet<QString> extensions = {
        QStringLiteral("htm"),
        QStringLiteral("html"),
        QStringLiteral("php"),
        QStringLiteral("xhtml"),
        QStringLiteral("svg")
    };
    return extensions.contains(suffix);
}
