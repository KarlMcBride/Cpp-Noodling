cmake_minimum_required(VERSION 3.7.2)
set(CMAKE_CXX_STANDARD 11)

project(bitset)

add_executable(${PROJECT_NAME} main.cpp)

target_link_libraries(${PROJECT_NAME} PUBLIC pthread)
