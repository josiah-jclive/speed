cmake_minimum_required(VERSION 3.20)
project(speed_decklink C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

add_library(speed_decklink SHARED speed_decklink.cpp speed_decklink.h)

if(WIN32)
  # Generate DeckLinkAPI.h and DeckLinkAPI_i.c from Blackmagic's IDL.
  # Needs midl.exe on PATH (a Visual Studio developer command prompt).
  set(SDK_DIR ${CMAKE_CURRENT_SOURCE_DIR}/sdk/win)
  set(GEN_DIR ${CMAKE_CURRENT_BINARY_DIR}/generated)
  file(MAKE_DIRECTORY ${GEN_DIR})
  add_custom_command(
    OUTPUT ${GEN_DIR}/DeckLinkAPI.h ${GEN_DIR}/DeckLinkAPI_i.c
    COMMAND midl /nologo /env x64 /I ${SDK_DIR} /h DeckLinkAPI.h /iid DeckLinkAPI_i.c
            /tlb DeckLinkAPI.tlb /proxy DeckLinkAPI_p.c /dlldata dlldata.c /out ${GEN_DIR}
            ${SDK_DIR}/DeckLinkAPI.idl
    DEPENDS ${SDK_DIR}/DeckLinkAPI.idl
    WORKING_DIRECTORY ${SDK_DIR}
    COMMENT "Generating DeckLink API headers with MIDL")
  target_sources(speed_decklink PRIVATE ${GEN_DIR}/DeckLinkAPI.h ${GEN_DIR}/DeckLinkAPI_i.c)
  target_include_directories(speed_decklink PRIVATE ${GEN_DIR})
  target_link_libraries(speed_decklink PRIVATE ole32 oleaut32 uuid)
elseif(APPLE)
  target_sources(speed_decklink PRIVATE sdk/mac/DeckLinkAPIDispatch.cpp)
  target_include_directories(speed_decklink PRIVATE sdk/mac)
  target_link_libraries(speed_decklink PRIVATE "-framework CoreFoundation")
  set_target_properties(speed_decklink PROPERTIES OSX_ARCHITECTURES "arm64;x86_64")
else()
  message(FATAL_ERROR "JSPEED's DeckLink bridge supports Windows and macOS.")
endif()
