#ifndef HMM_SYS_COREAPI_HMMSYSCORE_H
#define HMM_SYS_COREAPI_HMMSYSCORE_H
#include <iostream>
#include <fstream>
#include <atomic>
#include <string>
#include <vector>
#include <thread>
#include <mutex>
#include <map>
#include <functional>
#include "LibraryExport.h"
#include <boost/lockfree/queue.hpp>

#define DLL_VERSION "Beta20260629"

using WaveParams = struct WaveParams {
    float ao0_amplitude{0};
    float ao1_amplitude{0};
    float ao3_amplitude{0};
    float ao0_offset{0};
    float ao1_offset{0};
    float ao3_offset{0};
    bool ao3_static_mode{false};
    bool signal_invert{false};
    bool line_mode{false};
    int number_of_lines{0};
    int channel_start_delay{0};
    bool external_line_trigger{false};
    int line_frequency_hz{0};
    int pixel_per_line{0};
    int line_per_frame{0};
    int steps_per_stack{0};
    bool scan_dir{true};
    bool trig0_mode{false};
    int line_average{0};
    int frame_average{0};
    int oversample_head{0};
    int oversample_tail{0};
    int oversample_frame{0};
    int oversample_stack{0};
    bool channel0_enable{false};
    bool channel1_enable{false};
    bool channel2_enable{false};
    bool channel3_enable{false};
};

using RawParams = struct RawParams {
    int channel0_size{0};
    int channel1_size{0};
};

using FrequencyParams = struct FrequencyParams {
    float line_frequency_hz{0};
    int pos_time{0};
    int neg_time{0};
};

using MarkerParams = struct MarkerParams {
    bool marker1{false};
    bool marker2{false};
    bool marker3{false};
    bool marker4{false};
};

using LaserIOParams = struct LaserIOParams {
    bool laser_io1{false};
    bool laser_io2{false};
    bool laser_io3{false};
    bool laser_io4{false};
};

using CommonParams = struct CommonParams {
    int width{0};
    int height{0};
    int oversample_head{0};
    int oversample_tail{0};
    int continuous{0};
    int start{0};
    int steps_per_stack{0};
    int oversample_stack{0};
    bool is_static_mode{false};
    bool signal_invert{false};
    bool is_line_mode{false};
    int number_of_lines{0};
    bool is_neg{false};
    bool is_external_line_trigger{false};
    int channel0_size{0};
    int channel1_size{0};
};

using ResampledImage = struct ResampledImage {
    int channel{-1};
    int width{0};
    int height{0};
    int index0_line_number{0};
    int index0{0};
    int index1_line_number{0};
    int index1{0};
    std::vector<float> raw_data;
    std::vector<float> image_data;
};

using AOParams = struct AOParams {
    float ao_voltage{0};
    int ao_index{0};
};

using ErrorMessage = struct ErrorMessage {
    const char* error_message;
    int message_size;
};

class HMMSysCore {
private:
    HMMSysCore() = default;

//    ~HMMSysCore() = default;

    void* _data_insight_instance{};

    std::atomic<bool> _error_callback_enable{false};

    std::atomic<bool> _callback_enable{false};

    std::atomic<bool> _start_enable{false};
    
    std::atomic<int> _channel0_frame_index{-1};

    std::atomic<int> _channel1_frame_index{-1};

    std::atomic<int> _channel2_frame_index{-1};

    std::atomic<int> _channel3_frame_index{-1};

    std::atomic<int> _channel0_frame_number{0};

    std::atomic<int> _channel1_frame_number{0};

    std::atomic<int> _channel2_frame_number{0};

    std::atomic<int> _channel3_frame_number{0};

    std::atomic<bool> _request_exit{false};

    boost::lockfree::queue<ResampledImage*> _task_queue{40};

    boost::lockfree::queue<ResampledImage*> _resource_queue{40};

    boost::lockfree::queue<ErrorMessage*> _error_queue{40};

    std::function<void(const ResampledImage &)> _resampled_image_callback;

    std::function<void(const ErrorMessage &)> _error_message_callback;

    std::vector<std::thread> _threads;

    std::atomic<int> _task_queue_size{0};

    CommonParams  _common_params{};

    LaserIOParams _laser_io_params{};

    std::atomic<bool> _laser_io_flag{false};

    AOParams  _ao_params{};

    std::atomic<bool> _ao_flag{false};

    std::atomic<bool> _ao3_flag{false};

    std::atomic<float> _ao3_voltage{0};
    
    std::vector<bool> _channel_enable_vector{false, false, false, false};

    std::atomic<bool> _row_flag{false};

public:
    HMMSysCore(const HMMSysCore &) = delete;

    ~HMMSysCore() {
        user_stopped();
    }

    HMMSysCore &operator=(const HMMSysCore &) = delete;

    HMMSysCore(HMMSysCore &&) = delete;

    HMMSysCore &operator=(HMMSysCore &&) = delete;

    static HMMSysCore &get_instance();

    void set_resampled_image_callback(std::function<void(const ResampledImage &)> callback);

    void set_error_message_callback(std::function<void(const ErrorMessage &)> callback);

    bool initialize();

    void set_raw_size(RawParams& params);

    void get_frequency(FrequencyParams* params);

    void set_parameters(WaveParams& wave_params, int iteration_number);

    bool set_ao2_value(float voltage, float offset);

    void set_ao3_value(float voltage);

    void set_static_ao_value(float voltage, int dac_num=0);

    void set_laser_io(LaserIOParams& params);

    void get_markers(MarkerParams* params);

    void config_aux(bool aux_enable);

    void start_raw_scan();

    bool start_scan();

    void user_stopped();

    bool adc_calibration();

private:
    static std::map<std::string, int> calculate_timing_exact(int line_freq_hz, int pixel_per_line, int oversample_head, int oversample_tail);

    static std::vector<float> find_best_params(float target_clk);

    static std::map<std::string, int> calculate_triangle_params(float square_freq_hz, float input_v_peak, float sys_clk_hz=250000000.0, int margin_ticks=16, int guard_frames=1, int frac_bits=20);

    static int get_dec(double value, int fixedPrecision = 3);

    static std::vector<double> linspace(double min_voltage, double max_voltage, int total_intervals);

    static std::vector<uint32_t> generate_sawtooth_wave(float volt_value, float offset_voltage, int oversample_frame, int line_per_frame, float period_ns);

    static void generate_index_table(std::vector<uint32_t>& index_table, int pixel_per_line=1024, int oversample_head=32, int oversample_tail=32);

    void reset_ip();

    void clear_status();

    void read_image_frame();

    void read_image_data(int channel_index);

    void callback_image_data();

    void callback_error_message();

    void generate_wave(bool is_external_line_trigger, float ao0_amplitude, float ao1_amplitude, float ao3_amplitude, float ao0_offset, float ao1_offset, float ao3_offset, int line_frequency_hz, int pixel_per_line, int line_per_frame, int steps_per_stack, bool scan_dir, bool trig0_mode, int line_average, int frame_average, int oversample_head, int oversample_tail, int oversample_frame, int oversample_stack);

    void set_channel_params();

    void init_resource_queue();

    void polling();

    void polling_raw_data();

    void read_raw_data(int channel);
};


#endif //HMM_SYS_COREAPI_HMMSYSCORE_H
