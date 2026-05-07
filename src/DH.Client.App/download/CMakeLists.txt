cmake_minimum_required(VERSION 3.15)
project(GaussianFilter)

set(CMAKE_CXX_STANDARD 17)

add_library(GaussianFilter SHARED
    GaussianFilter.cpp
)
