#include <vector>
#include <cmath>
#include <algorithm>
#include <cstring>

// 高斯核生成函数
std::vector<double> generate_gaussian_kernel(int size, double sigma) {
    std::vector<double> kernel(size);
    int center = size / 2;
    double sum = 0.0;
    
    for (int i = 0; i < size; i++) {
        double x = i - center;
        kernel[i] = exp(-(x * x) / (2 * 1 * 1));
        sum += kernel[i];
    }
    
    // 归一化
    for (int i = 0; i < size; i++) {
        kernel[i] /= sum;
    }
    
    return kernel;
}

// 高斯滤波函数
int gaussian_filter(double* input, int length, double* output) {
    int kernel_size = 5;
    double sigma_val = 1;
    auto kernel = generate_gaussian_kernel(kernel_size, sigma_val);
    int half_kernel = kernel_size / 2;
    
    for (int i = 0; i < length; i++) {
        double sum = 0.0;
        
        for (int j = 0; j < kernel_size; j++) {
            int idx = i - half_kernel + j;
            
            // 边界处理
            if (idx < 0) idx = -idx;
            else if (idx >= length) idx = 2 * length - idx - 2;
            
            if (idx >= 0 && idx < length) {
                sum += input[idx] * kernel[j];
            }
        }
        output[i] = sum;
    }
    
    return 0; // 成功
}

extern "C" {
    __declspec(dllexport) int gaussianfilter_process(double* input, int length, double* output) {
        if (!input || !output || length <= 0) {
            return -1; // 错误
        }
        
        return gaussian_filter(input, length, output);
    }
}
