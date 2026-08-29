package io.listensphere.mobile.core.audio

object BluetoothPcm16Encoder {
    const val FRAME_SAMPLES = 480
    const val INPUT_FLOATS = FRAME_SAMPLES * 2
    const val OUTPUT_BYTES = FRAME_SAMPLES * 2

    fun outputBytes(channelCount: Int): Int {
        require(channelCount in 1..2)
        return FRAME_SAMPLES * channelCount * 2
    }

    fun encode(stereo: FloatArray, destination: ByteArray, channelCount: Int) {
        require(stereo.size >= INPUT_FLOATS)
        require(destination.size >= outputBytes(channelCount))
        for (sample in 0 until FRAME_SAMPLES) {
            if (channelCount == 1) {
                writeSample(destination, sample, (stereo[sample * 2] + stereo[sample * 2 + 1]) * 0.5f)
            } else {
                writeSample(destination, sample * 2, stereo[sample * 2])
                writeSample(destination, sample * 2 + 1, stereo[sample * 2 + 1])
            }
        }
    }

    fun encodeStereoToMono(stereo: FloatArray, destination: ByteArray) {
        encode(stereo, destination, 1)
    }

    private fun writeSample(destination: ByteArray, sampleIndex: Int, sample: Float) {
        val value = (sample.coerceIn(-1f, 1f) * 32767f).toInt()
        val offset = sampleIndex * 2
        destination[offset] = value.toByte()
        destination[offset + 1] = (value ushr 8).toByte()
    }
}
