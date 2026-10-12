#ifndef HMM_SYS_COREAPI_HMMSYSCOREBIND_H
#define HMM_SYS_COREAPI_HMMSYSCOREBIND_H

#include "HMMSysCore.h"

#ifdef __cplusplus

extern "C" {
#include "HMM_SYS_Core_LibraryExport.h"
#endif
typedef struct CResampledImage {
    int channel;
    int width;
    int height;
    int index0_line_number;
    int index0;
    int index1_line_number;
    int index1;
    const float *image_data;
    int image_size;
} CResampledImage;

typedef struct CResampledRow {
    int channel;
    const float *raw_data;
    int raw_size;
} CResampledRow;

typedef struct WaveParams WaveParams;
typedef struct FrequencyParams FrequencyParams;
typedef struct LaserIOParams LaserIOParams;
typedef struct MarkerParams MarkerParams;
typedef struct ErrorMessage ErrorMessage;
typedef struct RawParams RawParams;

HMM_SYS_COREAPI bool initialize();

typedef void (*ResampledImageCallback)(const CResampledImage *);

typedef void (*ResampledRawCallback)(const CResampledRow *);

typedef void (*ErrorMessageCallback)(const ErrorMessage *);

HMM_SYS_COREAPI void setResampledImageCallback(ResampledImageCallback callback);

HMM_SYS_COREAPI void setResampledRawCallback(ResampledRawCallback callback);

HMM_SYS_COREAPI void setErrorMessageCallback(ErrorMessageCallback callback);

HMM_SYS_COREAPI void getFrequency(FrequencyParams* params);

HMM_SYS_COREAPI void setRawSize(RawParams& params);

HMM_SYS_COREAPI void setParameters(WaveParams& wave_params, int iteration_number);

HMM_SYS_COREAPI bool setAo2Value(float voltage, float offset);

HMM_SYS_COREAPI void setAo3Value(float voltage);

HMM_SYS_COREAPI void setStaticAoValue(float voltage, int dac_num=0);

HMM_SYS_COREAPI void setLaserIo(LaserIOParams& params);

HMM_SYS_COREAPI void getMarkers(MarkerParams* params);

HMM_SYS_COREAPI void setAux(bool aux_enable);

HMM_SYS_COREAPI bool startScan();

HMM_SYS_COREAPI void stopScan();

HMM_SYS_COREAPI void startRawScan();

HMM_SYS_COREAPI bool adcCalibration();

#ifdef __cplusplus
}
#endif
#endif //HMM_SYS_COREAPI_HMMSYSCOREBIND_H
