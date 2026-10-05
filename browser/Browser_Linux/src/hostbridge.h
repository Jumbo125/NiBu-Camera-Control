#pragma once

#include <QObject>

class MainWindow;

class HostBridge : public QObject
{
    Q_OBJECT
public:
    explicit HostBridge(MainWindow *window);

    Q_INVOKABLE void minimize();
    Q_INVOKABLE void maximize();
    Q_INVOKABLE void restore();
    Q_INVOKABLE void setKiosk(bool enabled);
    Q_INVOKABLE void close();
    Q_INVOKABLE void exit();

private:
    MainWindow *m_window;
};
