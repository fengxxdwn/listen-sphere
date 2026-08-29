package io.listensphere.mobile.core.audio

object ImaAdpcmEncoder {
    const val SAMPLES_PER_FRAME = 480
    const val ENCODED_BYTES_PER_FRAME = 244
    private val indexTable = intArrayOf(-1,-1,-1,-1,2,4,6,8,-1,-1,-1,-1,2,4,6,8)
    private val stepTable = intArrayOf(7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767)

    fun encode(pcm16LittleEndian: ByteArray, destination: ByteArray) {
        encode(pcm16LittleEndian, destination, 1)
    }

    fun encodedBytes(channelCount: Int): Int {
        require(channelCount in 1..2)
        return ENCODED_BYTES_PER_FRAME * channelCount
    }

    fun encode(pcm16LittleEndian: ByteArray, destination: ByteArray, channelCount: Int) {
        require(channelCount in 1..2)
        require(pcm16LittleEndian.size == SAMPLES_PER_FRAME * channelCount * 2)
        require(destination.size >= encodedBytes(channelCount))
        destination.fill(0)
        for (channel in 0 until channelCount) {
            encodeChannel(pcm16LittleEndian, destination, channelCount, channel)
        }
    }

    private fun encodeChannel(
        pcm16LittleEndian: ByteArray,
        destination: ByteArray,
        channelCount: Int,
        channel: Int,
    ) {
        val outputOffset = channel * ENCODED_BYTES_PER_FRAME
        var predictor = readSample(pcm16LittleEndian, 0, channelCount, channel)
        var index = 0
        destination[outputOffset] = predictor.toByte()
        destination[outputOffset + 1] = (predictor shr 8).toByte()
        destination[outputOffset + 2] = 0
        destination[outputOffset + 3] = 0
        for (sampleIndex in 1 until SAMPLES_PER_FRAME) {
            val step = stepTable[index]
            var difference = readSample(
                pcm16LittleEndian, sampleIndex, channelCount, channel,
            ) - predictor
            var nibble = 0
            if (difference < 0) { nibble = 8; difference = -difference }
            var delta = step shr 3
            if (difference >= step) { nibble = nibble or 4; difference -= step; delta += step }
            if (difference >= step shr 1) { nibble = nibble or 2; difference -= step shr 1; delta += step shr 1 }
            if (difference >= step shr 2) { nibble = nibble or 1; delta += step shr 2 }
            predictor = (predictor + if (nibble and 8 != 0) -delta else delta).coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
            index = (index + indexTable[nibble]).coerceIn(0, 88)
            val nibbleIndex = sampleIndex - 1
            val byteIndex = outputOffset + 4 + nibbleIndex / 2
            if (nibbleIndex and 1 == 0) destination[byteIndex] = nibble.toByte()
            else destination[byteIndex] = (destination[byteIndex].toInt() or (nibble shl 4)).toByte()
        }
    }

    private fun readSample(pcm: ByteArray, sample: Int, channelCount: Int, channel: Int): Int {
        val offset = (sample * channelCount + channel) * 2
        return ((pcm[offset].toInt() and 0xff) or (pcm[offset + 1].toInt() shl 8)).toShort().toInt()
    }
}
