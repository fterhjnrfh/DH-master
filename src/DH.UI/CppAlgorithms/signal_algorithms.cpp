#include <vector>
#include <cmath>
#include <algorithm>
#include "signal_algorithms.h"

// Moving average filter implementation
int moving_average_filter(double* input, int length, double* output) {
    if (!input || !output || length <= 0) {
        return -1; // Error
    }
    
    int window_size = 5; // Default window size
    int half_window = window_size / 2;
    
    for (int i = 0; i < length; i++) {
        double sum = 0.0;
        int count = 0;
        
        for (int j = -half_window; j <= half_window; j++) {
            int idx = i + j;
            
            // Boundary handling: reflection
            if (idx < 0) idx = -idx;
            else if (idx >= length) idx = 2 * length - idx - 2;
            
            if (idx >= 0 && idx < length) {
                sum += input[idx];
                count++;
            }
        }
        
        if (count > 0) {
            output[i] = sum / count;
        } else {
            output[i] = input[i]; // Keep original value
        }
    }
    
    return 0; // Success
}

// Gaussian kernel generation function
std::vector<double> generate_gaussian_kernel(int size, double sigma) {
    std::vector<double> kernel(size);
    int center = size / 2;
    double sum = 0.0;
    
    for (int i = 0; i < size; i++) {
        double x = i - center;
        kernel[i] = exp(-(x * x) / (2 * sigma * sigma));
        sum += kernel[i];
    }
    
    // Normalization
    for (int i = 0; i < size; i++) {
        kernel[i] /= sum;
    }
    
    return kernel;
}

// Gaussian filter implementation
int gaussian_filter(double* input, int length, double sigma, int kernel_size, double* output) {
    if (!input || !output || length <= 0 || sigma <= 0 || kernel_size <= 0) {
        return -1; // Error
    }
    
    // Ensure kernel size is odd
    if (kernel_size % 2 == 0) kernel_size++;
    
    auto kernel = generate_gaussian_kernel(kernel_size, sigma);
    int half_kernel = kernel_size / 2;
    
    for (int i = 0; i < length; i++) {
        double sum = 0.0;
        
        for (int j = 0; j < kernel_size; j++) {
            int idx = i - half_kernel + j;
            
            // Boundary handling: reflection
            if (idx < 0) idx = -idx;
            else if (idx >= length) idx = 2 * length - idx - 2;
            
            if (idx >= 0 && idx < length) {
                sum += input[idx] * kernel[j];
            }
        }
        
        output[i] = sum;
    }
    
    return 0; // Success
}

// Median filter implementation
int median_filter(double* input, int length, double* output) {
    if (!input || !output || length <= 0) {
        return -1; // Error
    }
    
    int window_size = 3; // Default window size
    int half_window = window_size / 2;
    std::vector<double> window(window_size);
    
    for (int i = 0; i < length; i++) {
        // Build window
        for (int j = 0; j < window_size; j++) {
            int idx = i - half_window + j;
            
            // Boundary handling: reflection
            if (idx < 0) idx = -idx;
            else if (idx >= length) idx = 2 * length - idx - 2;
            
            if (idx >= 0 && idx < length) {
                window[j] = input[idx];
            } else {
                window[j] = 0.0; // Default value
            }
        }
        
        // Sort and get median
        std::sort(window.begin(), window.end());
        output[i] = window[window_size / 2];
    }
    
    return 0; // Success
}

// Signal smoothing implementation
int signal_smooth(double* input, int length, double* output) {
    if (!input || !output || length <= 0) {
        return -1; // Error
    }
    
    int window_size = 5; // Default window size
    double smooth_factor = 0.5; // Smoothing factor
    int half_window = window_size / 2;
    
    for (int i = 0; i < length; i++) {
        double sum = 0.0;
        double weight_sum = 0.0;
        
        for (int j = 0; j < window_size; j++) {
            int idx = i - half_window + j;
            double distance = abs(j - half_window) / (double)half_window;
            double weight = 1.0 - (smooth_factor * distance);
            
            // Boundary handling: reflection
            if (idx < 0) idx = -idx;
            else if (idx >= length) idx = 2 * length - idx - 2;
            
            if (idx >= 0 && idx < length) {
                sum += input[idx] * weight;
                weight_sum += weight;
            }
        }
        
        if (weight_sum > 0) {
            output[i] = sum / weight_sum;
        } else {
            output[i] = input[i]; // Keep original value
        }
    }
    
    return 0; // Success
}