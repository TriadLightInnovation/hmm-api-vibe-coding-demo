#include <iostream>
#include <csignal>
#include "HMMSysCoreBind.h"
#include <tiffio.h>
#include <future>
#include <memory>
#include "json.hpp"

using json = nlohmann::json;
std::atomic<bool> flag(true);
std::atomic<int> num(0);
int image_num = 20;

static void signal_handler(int signal) {
    std::cout << "Exit trigger: " << signal << std::endl;
    HMM_SYS_COREAPI::stopScan();
    flag = false;
}

void write_tiff(const std::string& file_path, const float *data, int width, int height) {

    TIFF* tif = TIFFOpen(file_path.c_str(), "w");
    if (tif == nullptr) {
        std::cerr << "Failed to open TIFF file: " << file_path << std::endl;
        return;
    }

    TIFFSetField(tif, TIFFTAG_IMAGEWIDTH, width);
    TIFFSetField(tif, TIFFTAG_IMAGELENGTH, height);
    TIFFSetField(tif, TIFFTAG_SAMPLESPERPIXEL, 1);
    TIFFSetField(tif, TIFFTAG_BITSPERSAMPLE, 32);
    TIFFSetField(tif, TIFFTAG_ORIENTATION, ORIENTATION_TOPLEFT);
    TIFFSetField(tif, TIFFTAG_PLANARCONFIG, PLANARCONFIG_CONTIG);
    TIFFSetField(tif, TIFFTAG_PHOTOMETRIC, PHOTOMETRIC_MINISBLACK);
    TIFFSetField(tif, TIFFTAG_COMPRESSION, COMPRESSION_NONE);
    TIFFSetField(tif, TIFFTAG_SAMPLEFORMAT, SAMPLEFORMAT_IEEEFP);

    if (TIFFWriteEncodedStrip(tif, 0, const_cast<float*>(data), width * height * sizeof(float)) < 0) {
        std::cerr << "Error writing TIFF data." << std::endl;
        TIFFClose(tif);
    }

    TIFFClose(tif);
}

void resampled_image_callback(const CResampledImage* image) {
    try {
        std::cout << "Channel index: " << image->channel << std::endl;
        std::cout << "Width: " << image->width << std::endl;
        std::cout << "Height: " << image->height << std::endl;
        std::cout << "Index0 Line number: " << image->index0_line_number << std::endl;
        std::cout << "######################Index0: " << image->index0 << std::endl;
        if (image->index1_line_number != NULL) {
            std::cout << "Index1 Line number: " << image->index1_line_number << std::endl;
            std::cout << "*****************Index1: " << image->index1 << std::endl;
        }
        if (num <= image_num) {
            std::string output_file_path =
                    "image_data/channel" + std::to_string(image->channel) + "-data_num" +
                    std::to_string(num) +
                    ".tiff";
            write_tiff(output_file_path, image->image_data, image->width, int(image->image_size/image->width));
            num = num + 1;
        }
        if (num == 10) {
            HMM_SYS_COREAPI::setStaticAoValue(4.5, 2);
            HMM_SYS_COREAPI::setAo3Value(4.5);
        }
    } catch (const std::runtime_error& e) {
        std::cerr << "Runtime error: " << e.what() << std::endl;
    }
}

int main(int argc, char** argv) {
    std::cout << "Hello, World!" << std::endl;
    uint32_t sleep_time;
    int cycle_num = 1;

    if (argc == 1) {
        sleep_time = 10;
    } else if (argc == 2) {
        try {
            cycle_num = std::stol(argv[1]);
        } catch (const std::exception& e) {
            std::cerr << "Error: Invalid argument" << e.what() << ". e.g. (HMM_SYS_CoreAPI.exe <system_cycle_number> <system_running_time>)" << std::endl;
            return 1;
        }
    } else if (argc == 3) {
        try {
            cycle_num = std::stol(argv[1]);
            sleep_time = std::stol(argv[2]);
        } catch (const std::exception& e) {
            std::cerr << "Error: Invalid argument" << e.what() << ". e.g. (HMM_SYS_CoreAPI.exe <system_cycle_number> <system_running_time>)" << std::endl;
            return 1;
        }
    } else if (argc == 4) {
        try {
            cycle_num = std::stol(argv[1]);
            sleep_time = std::stol(argv[2]);
            image_num = std::stol(argv[3]);
        } catch (const std::exception& e) {
            std::cerr << "Error: Invalid argument" << e.what() << ". e.g. (HMM_SYS_CoreAPI.exe <system_cycle_number> <system_running_time> <save_image_number>)" << std::endl;
            return 1;
        }
    } else {
        std::cerr << "Error: Invalid argument. e.g. (HMM_SYS_CoreAPI.exe <system_running_time>)" << std::endl;
        return 1;
    }
    for (int i = 0; i < cycle_num; ++i) {
        if (flag) {
            std::cout << "System cycle: " << i << std::endl;
            if (HMM_SYS_COREAPI::initialize()) {
                std::cout << "sampling rate = 1GS/s" << std::endl;
                std::string setting_path_name = "io_settings.json";
                std::fstream file(setting_path_name, std::ios::in);
                if (!file.is_open()) {
                    std::cerr << "Failed to open file: " << setting_path_name << std::endl;
                    return -1;
                }
                json setting;
                try {
                    setting = json::parse(file);
                    file.close();
                } catch (const nlohmann::json::parse_error& e) {
                    std::cerr << "JSON parse error: " << e.what() << std::endl;
                    file.close();
                    return -1;
                } catch (const std::exception& e) {
                    std::cerr << "Unexpected error during JSON parsing: " << e.what() << std::endl;
                    file.close();
                    return -1;
                }
                WaveParams params;
                params.ao0_amplitude = setting["ao0 amplitude"];
                params.ao1_amplitude = setting["ao1 amplitude"];
//                params.ao2_amplitude = setting["ao2 amplitude"];
                params.ao3_amplitude = setting["ao3 amplitude"];
                params.ao0_offset = setting["ao0 offset"];
                params.ao1_offset = setting["ao1 offset"];
//                params.ao2_offset = setting["ao2 offset"];
                params.ao3_offset = setting["ao3 offset"];
                params.ao3_static_mode = setting["ao3 static mode"];
                params.signal_invert = setting["signal invert"];
                params.line_mode = setting["line mode"];
                params.number_of_lines = setting["number of lines"];
                params.channel_start_delay = setting["channel start delay"];
                params.external_line_trigger  = setting["external line trigger"];
                params.line_frequency_hz = setting["line frequency hz"];
                params.pixel_per_line = setting["pixel per line"];
                params.line_per_frame = setting["line per frame"];
                params.steps_per_stack = setting["steps per stack"];
                params.scan_dir = setting["scan dir"];
                params.trig0_mode = setting["trig0 mode"];
                params.line_average = setting["line average"];
                params.frame_average = setting["frame average"];
                params.oversample_head = setting["oversample head"];
                params.oversample_tail = setting["oversample tail"];
                params.oversample_frame = setting["oversample frame"];
                params.oversample_stack = setting["oversample stack"];
                params.channel0_enable = setting["channel0 enable"];
                params.channel1_enable = setting["channel1 enable"];
                params.channel2_enable = setting["channel2 enable"];
                params.channel3_enable = setting["channel3 enable"];
                int iteration_number = setting["iteration number"];
                bool raw_flag = setting["raw mode"];
                std::cout << "raw mode:" << raw_flag << std::endl;
                if (raw_flag) {
                    try {
                        bool adc_ret = HMM_SYS_COREAPI::adcCalibration();
                        std::cout << "ADC calibration: " << adc_ret << std::endl;

                        RawParams raw_params;
                        raw_params.channel0_size = setting["channel0 size"];
                        raw_params.channel1_size = setting["channel1 size"];
                        HMM_SYS_COREAPI::setRawSize(raw_params);

                        bool ret = HMM_SYS_COREAPI::setAo2Value(setting["ao2 amplitude"], setting["ao2 offset"]);
                        std::cout << "Set AO2 value: " << ret << std::endl;

                        HMM_SYS_COREAPI::setParameters(params, iteration_number);

                        HMM_SYS_COREAPI::setStaticAoValue(setting["ao voltage"], setting["ao index"]);

                        LaserIOParams laser_io_params;
                        laser_io_params.laser_io1 = setting["laser io1"];
                        laser_io_params.laser_io2 = setting["laser io2"];
                        laser_io_params.laser_io3 = setting["laser io3"];
                        laser_io_params.laser_io4 = setting["laser io4"];
                        HMM_SYS_COREAPI::setLaserIo(laser_io_params);

                        MarkerParams marker_params;
                        HMM_SYS_COREAPI::getMarkers(&marker_params);
                        std::cout << "Channel0 Marker in: " << marker_params.marker1 << std::endl;
                        std::cout << "Channel1 Marker in: " << marker_params.marker2 << std::endl;
                        std::cout << "Channel2 Marker in: " << marker_params.marker3 << std::endl;
                        std::cout << "Channel3 Marker in: " << marker_params.marker4 << std::endl;

                        HMM_SYS_COREAPI::setResampledRawCallback([](const CResampledRow *raw) {
                            std::cout << "Channel index: " << raw->channel << std::endl;
                            std::string output_file_path = "image_data/raw_data_" + std::to_string(raw->channel) + ".bin";
                            std::ofstream f(output_file_path, std::ios::out | std::ios::binary);
                            if (f.is_open()) {
                             long long chunk_size = 536870912; // 512MB

                             if (raw->raw_size > chunk_size) {
                                 f.write(reinterpret_cast<const char *>(raw->raw_data), chunk_size);
                                 f.flush();

                                 f.write(reinterpret_cast<const char *>(raw->raw_data + chunk_size),
                                         raw->raw_size - chunk_size);
                                 f.flush();
                             } else {
                                 f.write(reinterpret_cast<const char *>(raw->raw_data), raw->raw_size);
                                 f.flush();
                             }

                             f.close();
                            }
                        });
                        HMM_SYS_COREAPI::startRawScan();
                        std::signal(SIGTERM, signal_handler);
                        std::signal(SIGINT, signal_handler);

                        std::this_thread::sleep_for(std::chrono::seconds(sleep_time));
                        std::cout << "Stop polling..." << std::endl;
                        HMM_SYS_COREAPI::stopScan();
                    } catch (const std::runtime_error& e) {
                        std::cerr << "Runtime error: " << e.what() << std::endl;
                    }
                } else {
                    try {
                        bool ret = HMM_SYS_COREAPI::setAo2Value(setting["ao2 amplitude"], setting["ao2 offset"]);
                        std::cout << "Set AO2 value: " << ret << std::endl;

                        std::this_thread::sleep_for(std::chrono::seconds(1));

                        FrequencyParams freq_params;
                        HMM_SYS_COREAPI::getFrequency(&freq_params);
                        std::cout << "pos time:" << freq_params.pos_time << std::endl;
                        std::cout << "neg time:" << freq_params.neg_time << std::endl;
                        std::cout << "Frequency: " << freq_params.line_frequency_hz << " Hz" << std::endl;
                        if (freq_params.pos_time == -1) {
                            std::cout << "Frequency is abnormal" << std::endl;
                            return -1;
                        }

                        HMM_SYS_COREAPI::setAux(setting["aux enable"]);
                        HMM_SYS_COREAPI::setParameters(params, iteration_number);
                        if (HMM_SYS_COREAPI::startScan()) {
                            HMM_SYS_COREAPI::setStaticAoValue(setting["ao voltage"], setting["ao index"]);

                            LaserIOParams laser_io_params;
                            laser_io_params.laser_io1 = setting["laser io1"];
                            laser_io_params.laser_io2 = setting["laser io2"];
                            laser_io_params.laser_io3 = setting["laser io3"];
                            laser_io_params.laser_io4 = setting["laser io4"];
                            HMM_SYS_COREAPI::setLaserIo(laser_io_params);

                            MarkerParams marker_params;
                            HMM_SYS_COREAPI::getMarkers(&marker_params);
                            std::cout << "Channel0 Marker in: " << marker_params.marker1 << std::endl;
                            std::cout << "Channel1 Marker in: " << marker_params.marker2 << std::endl;
                            std::cout << "Channel2 Marker in: " << marker_params.marker3 << std::endl;
                            std::cout << "Channel3 Marker in: " << marker_params.marker4 << std::endl;

                            HMM_SYS_COREAPI::setErrorMessageCallback([](const ErrorMessage *msg) {
                                std::string error_message = std::string(msg->error_message, msg->message_size);
                                std::cout << "Error message: " << error_message << std::endl;
                            });

                            HMM_SYS_COREAPI::setResampledImageCallback(resampled_image_callback);

                            std::signal(SIGTERM, signal_handler);
                            std::signal(SIGINT, signal_handler);

                            std::this_thread::sleep_for(std::chrono::seconds(sleep_time));
                            std::cout << "Stop polling..." << std::endl;
                            HMM_SYS_COREAPI::stopScan();
                        }

                    } catch (const std::runtime_error& e) {
                        std::cerr << "Runtime error: " << e.what() << std::endl;
                    } catch (...) {
                        std::cerr << "Unknown error" << std::endl;
                    }

                }

            } else {
                std::cout << "Failed to initialize" << std::endl;
            }
        }
    }


    return 0;
}
