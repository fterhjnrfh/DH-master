#ifndef SIGNAL_ALGORITHMS_H
#define SIGNAL_ALGORITHMS_H

#ifdef __cplusplus
extern "C" {
#endif

// Moving average filter
int moving_average_filter(double* input, int length, double* output);

// Gaussian filter
int gaussian_filter(double* input, int length, double sigma, int kernel_size, double* output);

// Median filter
int median_filter(double* input, int length, double* output);

// Signal smoothing
int signal_smooth(double* input, int length, double* output);

#ifdef __cplusplus
}
#endif

#endif // SIGNAL_ALGORITHMS_H