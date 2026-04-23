#include "mainwindow.h"
#include "hostbridge.h"

#include <QAction>
#include <QCloseEvent>
#include <QFile>
#include <QKeySequence>
#include <QShortcut>
#include <QVBoxLayout>
#include <QWebChannel>
#include <QWebEnginePage>
#include <QWebEngineProfile>
#include <QWebEngineScript>
#include <QWebEngineScriptCollection>
#include <QWebEngineSettings>
#include <QWebEngineView>
#include <QWidget>

MainWindow::MainWindow(const AppConfig &config, const QString &configPath, QWidget *parent)
    : QMainWindow(parent)
    , m_config(config)
    , m_configPath(configPath)
{
    initializeUi();
    initializeWebView();
    installBridge();
    installDevToolsShortcuts();
}

void MainWindow::initializeUi()
{
    setWindowTitle(m_config.effectiveTitle());
    resize(1280, 800);
    applyWindowIcon();

    m_view = new QWebEngineView(this);
    setCentralWidget(m_view);
}

void MainWindow::applyWindowIcon()
{
    QString iconPath = m_config.resolveCustomIconPath(m_configPath);
    if (iconPath.isEmpty()) {
        iconPath = m_config.resolveFallbackIconPath();
    }

    if (!iconPath.isEmpty()) {
        setWindowIcon(QIcon(iconPath));
    }
}

void MainWindow::initializeWebView()
{
    auto *settings = m_view->settings();
    settings->setAttribute(QWebEngineSettings::JavascriptEnabled, true);
    settings->setAttribute(QWebEngineSettings::LocalContentCanAccessFileUrls, true);
    settings->setAttribute(QWebEngineSettings::LocalContentCanAccessRemoteUrls, true);
    settings->setAttribute(QWebEngineSettings::FullScreenSupportEnabled, true);

    const QString target = m_config.resolveStartupTarget(m_configPath);
    m_view->load(QUrl(target));
}

void MainWindow::installBridge()
{
    m_channel = new QWebChannel(this);
    m_bridge = new HostBridge(this);
    m_channel->registerObject(QStringLiteral("hostBridge"), m_bridge);
    m_view->page()->setWebChannel(m_channel);

    QFile qwebchannelJs(QStringLiteral(":/qtwebchannel/qwebchannel.js"));
    if (!qwebchannelJs.open(QIODevice::ReadOnly)) {
        return;
    }

    QString scriptSource = QString::fromUtf8(qwebchannelJs.readAll());
    scriptSource += QStringLiteral(R"JS(
(function () {
  if (window.__fotoboxHostBridgeInstalled) {
    return;
  }

  function exposeBridge() {
    if (!window.qt || !window.qt.webChannelTransport || typeof QWebChannel === 'undefined') {
      return false;
    }

    new QWebChannel(window.qt.webChannelTransport, function (channel) {
      var bridge = channel.objects.hostBridge;
      if (!bridge) {
        return;
      }

      window.hostApp = {
        minimize: function () { bridge.minimize(); },
        maximize: function () { bridge.maximize(); },
        restore: function () { bridge.restore(); },
        setKiosk: function (enabled) { bridge.setKiosk(!!enabled); },
        close: function () { bridge.close(); },
        exit: function () { bridge.exit(); }
      };

      window.__fotoboxHostBridgeInstalled = true;
      window.dispatchEvent(new Event('hostAppReady'));
    });

    return true;
  }

  if (!exposeBridge()) {
    document.addEventListener('DOMContentLoaded', exposeBridge, { once: true });
  }
})();
)JS");

    QWebEngineScript script;
    script.setName(QStringLiteral("fotobox-host-bridge"));
    script.setInjectionPoint(QWebEngineScript::DocumentCreation);
    script.setWorldId(QWebEngineScript::MainWorld);
    script.setRunsOnSubFrames(false);
    script.setSourceCode(scriptSource);
    m_view->page()->scripts().insert(script);
}

void MainWindow::installDevToolsShortcuts()
{
    if (!m_config.allowDevTools) {
        return;
    }

    auto *shortcut1 = new QShortcut(QKeySequence(QStringLiteral("F12")), this);
    connect(shortcut1, &QShortcut::activated, this, &MainWindow::openDevTools);

    auto *shortcut2 = new QShortcut(QKeySequence(QStringLiteral("Ctrl+Shift+I")), this);
    connect(shortcut2, &QShortcut::activated, this, &MainWindow::openDevTools);
}

void MainWindow::openDevTools()
{
    if (!m_config.allowDevTools) {
        return;
    }

    if (!m_devToolsWindow) {
        m_devToolsWindow = new QMainWindow();
        m_devToolsWindow->setAttribute(Qt::WA_DeleteOnClose);
        m_devToolsWindow->resize(1200, 800);
        m_devToolsWindow->setWindowTitle(QStringLiteral("DevTools - %1").arg(windowTitle()));
        m_devToolsWindow->setWindowIcon(windowIcon());

        m_devToolsView = new QWebEngineView(m_devToolsWindow);
        m_devToolsWindow->setCentralWidget(m_devToolsView);
        m_devToolsView->page()->setInspectedPage(m_view->page());

        connect(m_devToolsWindow, &QObject::destroyed, this, [this]() {
            m_devToolsWindow = nullptr;
            m_devToolsView = nullptr;
        });
    }

    m_devToolsWindow->show();
    m_devToolsWindow->raise();
    m_devToolsWindow->activateWindow();
}

void MainWindow::minimizeWindow()
{
    showMinimized();
}

void MainWindow::restoreWindow()
{
    setKioskMode(false);
}

void MainWindow::setKioskMode(bool enabled)
{
    m_isKiosk = enabled;

    if (enabled) {
        setWindowFlag(Qt::FramelessWindowHint, true);
        setWindowFlag(Qt::WindowStaysOnTopHint, true);
        showFullScreen();
    } else {
        setWindowFlag(Qt::FramelessWindowHint, false);
        setWindowFlag(Qt::WindowStaysOnTopHint, false);
        showMaximized();
    }
}

void MainWindow::requestClose()
{
    close();
}

void MainWindow::closeEvent(QCloseEvent *event)
{
    if (m_devToolsWindow) {
        m_devToolsWindow->close();
    }
    event->accept();
}
