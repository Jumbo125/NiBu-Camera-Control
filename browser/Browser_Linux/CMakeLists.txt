cmake_minimum_required(VERSION 3.21)
project(FotoboxQtWebEngineHost VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)
set(CMAKE_AUTOUIC ON)

find_package(Qt6 REQUIRED COMPONENTS Widgets WebEngineWidgets WebChannel)

qt_standard_project_setup()

qt_add_executable(fotobox-qtwebengine-host
    src/main.cpp
    src/appconfig.h
    src/appconfig.cpp
    src/hostbridge.h
    src/hostbridge.cpp
    src/mainwindow.h
    src/mainwindow.cpp
)

target_link_libraries(fotobox-qtwebengine-host PRIVATE
    Qt6::Widgets
    Qt6::WebEngineWidgets
    Qt6::WebChannel
)

if(UNIX AND NOT APPLE)
    target_compile_definitions(fotobox-qtwebengine-host PRIVATE QT_NO_KEYWORDS)
endif()

configure_file(${CMAKE_SOURCE_DIR}/init.json ${CMAKE_BINARY_DIR}/init.json COPYONLY)
file(COPY ${CMAKE_SOURCE_DIR}/wwwroot DESTINATION ${CMAKE_BINARY_DIR})
file(COPY ${CMAKE_SOURCE_DIR}/assets DESTINATION ${CMAKE_BINARY_DIR})

install(TARGETS fotobox-qtwebengine-host RUNTIME DESTINATION .)
install(FILES ${CMAKE_SOURCE_DIR}/init.json DESTINATION .)
install(DIRECTORY ${CMAKE_SOURCE_DIR}/wwwroot DESTINATION .)
install(DIRECTORY ${CMAKE_SOURCE_DIR}/assets DESTINATION .)
