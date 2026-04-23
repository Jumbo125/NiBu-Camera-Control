#include "appconfig.h"
#include "mainwindow.h"

#include <QApplication>
#include <QCommandLineOption>
#include <QCommandLineParser>
#include <QCoreApplication>
#include <QDir>
#include <QFileInfo>
#include <QUrl>

static bool parseBoolValue(const QString &raw, bool fallback = false)
{
    const QString v = raw.trimmed().toLower();

    if (v == QStringLiteral("1") ||
        v == QStringLiteral("true") ||
        v == QStringLiteral("yes") ||
        v == QStringLiteral("on")) {
        return true;
    }

    if (v == QStringLiteral("0") ||
        v == QStringLiteral("false") ||
        v == QStringLiteral("no") ||
        v == QStringLiteral("off")) {
        return false;
    }

    return fallback;
}

static bool looksLikeWebTarget(const QString &value)
{
    const QString trimmed = value.trimmed();
    if (trimmed.isEmpty()) {
        return false;
    }

    if (trimmed.startsWith(QStringLiteral("http://"), Qt::CaseInsensitive) ||
        trimmed.startsWith(QStringLiteral("https://"), Qt::CaseInsensitive)) {
        return true;
    }

    if (trimmed.contains(u'\\')) {
        return false;
    }

    if (trimmed.startsWith(QStringLiteral("localhost"), Qt::CaseInsensitive)) {
        return true;
    }

    if (!trimmed.isEmpty() && trimmed.front().isDigit()) {
        return true;
    }

    const QString hostPortPath = trimmed.section(u'/', 0, 0);
    const QString hostOnly = hostPortPath.section(u':', 0, 0);
    return hostOnly.contains(u'.') && !hostOnly.contains(u' ');
}

static QString combineUrlAndPort(const QString &rawUrl, int portOverride)
{
    const QString trimmed = rawUrl.trimmed();
    if (trimmed.isEmpty()) {
        return trimmed;
    }

    if (portOverride <= 0 || portOverride >= 65536) {
        return trimmed;
    }

    if (!looksLikeWebTarget(trimmed)) {
        return trimmed;
    }

    QString candidate = trimmed;
    if (!candidate.startsWith(QStringLiteral("http://"), Qt::CaseInsensitive) &&
        !candidate.startsWith(QStringLiteral("https://"), Qt::CaseInsensitive)) {
        candidate.prepend(QStringLiteral("http://"));
    }

    QUrl url(candidate);
    if (!url.isValid() || url.scheme().isEmpty()) {
        return trimmed;
    }

    if (url.port() == -1) {
        url.setPort(portOverride);
    }

    return url.toString();
}

static void applyCommandLineOverrides(AppConfig &config, const QCommandLineParser &parser,
                                      const QCommandLineOption &urlOption,
                                      const QCommandLineOption &portOption,
                                      const QCommandLineOption &kioskOption)
{
    QString urlOverride;
    int portOverride = -1;

    if (parser.isSet(urlOption)) {
        urlOverride = parser.value(urlOption).trimmed();
    }

    if (parser.isSet(portOption)) {
        bool ok = false;
        const int port = parser.value(portOption).trimmed().toInt(&ok);
        if (ok && port > 0 && port < 65536) {
            portOverride = port;
            config.defaultPort = port;
        }
    }

    if (!urlOverride.isEmpty()) {
        config.url = combineUrlAndPort(urlOverride, portOverride);
    }

    if (parser.isSet(kioskOption)) {
        config.kiosk = parseBoolValue(parser.value(kioskOption), config.kiosk);
    }
}

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    app.setApplicationName(QStringLiteral("fotobox-qtwebengine-host"));
    app.setApplicationDisplayName(AppConfig::hardcodedTitle);

    const QString configPath =
        QFileInfo(QDir(QCoreApplication::applicationDirPath()),
                  QStringLiteral("init.json")).absoluteFilePath();

    AppConfig config = AppConfig::load(configPath);

    QCommandLineParser parser;
    parser.setApplicationDescription(QStringLiteral("Fotobox Qt WebEngine Host"));
    parser.addHelpOption();
    parser.addVersionOption();

    QCommandLineOption urlOption(
        QStringList() << QStringLiteral("url"),
        QStringLiteral("Override startup URL or local file path."),
        QStringLiteral("url"));

    QCommandLineOption portOption(
        QStringList() << QStringLiteral("port"),
        QStringLiteral("Override default port. If --url is a web target without explicit port, this port is injected into the URL."),
        QStringLiteral("port"));

    QCommandLineOption kioskOption(
        QStringList() << QStringLiteral("kiosk"),
        QStringLiteral("Override kiosk mode (true/false, 1/0, yes/no, on/off)."),
        QStringLiteral("bool"));

    parser.addOption(urlOption);
    parser.addOption(portOption);
    parser.addOption(kioskOption);
    parser.process(app);

    applyCommandLineOverrides(config, parser, urlOption, portOption, kioskOption);

    MainWindow window(config, configPath);

    if (config.kiosk) {
        window.showFullScreen();
    } else {
        window.showMaximized();
    }

    return app.exec();
}
