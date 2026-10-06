package com.fy1ng.remotedisplayswitch.pairing

import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

interface PairWire {
    fun read(): JSONObject
    fun write(message: JSONObject)
}

class SocketPairWire(socket: Socket) : PairWire {
    private val input = socket.getInputStream()
    private val output = socket.getOutputStream()
    override fun read(): JSONObject {
        val bytes = ByteArrayOutputStream()
        while (true) {
            val next = input.read()
            require(next >= 0) { "Disconnected" }
            if (next == 10) break
            require(bytes.size() < 16384) { "Frame too large" }
            bytes.write(next)
        }
        return JSONObject(Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes.toByteArray())).toString())
    }
    override fun write(message: JSONObject) {
        val bytes = message.toString().toByteArray(Charsets.UTF_8)
        require(bytes.size <= 16384)
        output.write(bytes)
        output.write(10)
        output.flush()
    }
}
