#ifndef GAUSSIANFILTER_H
#define GAUSSIANFILTER_H

#ifdef __cplusplus
extern "C" {
#endif

int gaussianfilter_process(double* input, int length, double* output);

#ifdef __cplusplus
}
#endif

#endif // GAUSSIANFILTER_H
