#pragma once

#include "appconfig.h"

#include <QMainWindow>
#include <QPointer>

class HostBridge;
class QWebChannel;
class QWebEngineView;
class QWebEngineScript;
class QWidget;

class MainWindow : public QMainWindow
{
    Q_OBJECT
public:
    explicit MainWindow(const AppConfig &config, const QString &configPath, QWidget *parent = nullptr);
    ~MainWindow() override = default;

    void minimizeWindow();
    void restoreWindow();
    void setKioskMode(bool enabled);
    void requestClose();

protected:
    void closeEvent(QCloseEvent *event) override;

private:
    void initializeUi();
    void initializeWebView();
    void installBridge();
    void installDevToolsShortcuts();
    void openDevTools();
    void applyWindowIcon();

    AppConfig m_config;
    QString m_configPath;
    bool m_isKiosk = false;
    QWebEngineView *m_view = nullptr;
    QWebChannel *m_channel = nullptr;
    HostBridge *m_bridge = nullptr;
    QPointer<QMainWindow> m_devToolsWindow;
    QPointer<QWebEngineView> m_devToolsView;
};
