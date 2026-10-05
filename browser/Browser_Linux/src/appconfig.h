#pragma once

#include <QCoreApplication>
#include <QFile>
#include <QJsonDocument>
#include <QJsonObject>
#include <QRegularExpression>
#include <QTextStream>
#include <QUrl>

class AppConfig
{
public:
    static inline const QString hardcodedTitle = QStringLiteral("Fotobox Kiosk Host");
    static inline const QString hardcodedIconRelativePath = QStringLiteral("assets/app.png");

    QString url;
    QString defaultUrl = QStringLiteral("http://127.0.0.1");
    int defaultPort = 8080;
    QString localIndexPath = QStringLiteral("wwwroot/index.html");
    bool kiosk = false;
    QString title;
    QString icon;
    bool allowDevTools = false;

    static AppConfig load(const QString &configPath);
    QString effectiveTitle() const;
    QString resolveStartupTarget(const QString &configPath) const;
    QString resolveCustomIconPath(const QString &configPath) const;
    QString resolveFallbackIconPath() const;

private:
    static bool tryResolveConfigTarget(const QString &rawValue,
                                       const QString &configDirectory,
                                       QString &target,
                                       bool requireExistingFile = false);
    static bool tryNormalizeWebUrl(const QString &rawValue, QString &normalizedUrl);
    static bool looksLikeHostOrIp(const QString &value);
    static bool hasLikelyLocalFileExtension(const QString &value);
    QString buildDefaultUrl() const;
};
