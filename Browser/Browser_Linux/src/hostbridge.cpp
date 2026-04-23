#include "hostbridge.h"
#include "mainwindow.h"

HostBridge::HostBridge(MainWindow *window)
    : QObject(window)
    , m_window(window)
{
}

void HostBridge::minimize()
{
    m_window->minimizeWindow();
}

void HostBridge::maximize()
{
    m_window->setKioskMode(true);
}

void HostBridge::restore()
{
    m_window->restoreWindow();
}

void HostBridge::setKiosk(bool enabled)
{
    m_window->setKioskMode(enabled);
}

void HostBridge::close()
{
    m_window->requestClose();
}

void HostBridge::exit()
{
    m_window->requestClose();
}
