cmake_minimum_required(VERSION 3.20)
project(FpsHotkeyExt VERSION 2.3.0 LANGUAGES C CXX)
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "FpsHotkeyExt requires an x64 build")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(MINHOOK_ROOT vendor/MinHook/src)
set(MINHOOK_SOURCES ${MINHOOK_ROOT}/buffer.c ${MINHOOK_ROOT}/hook.c
    ${MINHOOK_ROOT}/trampoline.c ${MINHOOK_ROOT}/hde/hde64.c)
add_library(FpsHotkeyExt SHARED dllmain.cpp ${MINHOOK_SOURCES})

target_compile_definitions(FpsHotkeyExt PRIVATE WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
target_compile_options(FpsHotkeyExt PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/W4>)
target_link_libraries(FpsHotkeyExt PRIVATE user32)

set_target_properties(FpsHotkeyExt PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

include(CTest)
if(BUILD_TESTING)
    add_executable(FpsHotkeyExtTests regression_tests.cpp ${MINHOOK_SOURCES})
    target_compile_definitions(FpsHotkeyExtTests PRIVATE WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
    target_link_libraries(FpsHotkeyExtTests PRIVATE user32)
    add_test(NAME FpsHotkeyExtRegression COMMAND FpsHotkeyExtTests)
    set_tests_properties(FpsHotkeyExtRegression PROPERTIES WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}")
endif()
