package com.everyonesoftware.storage.service.pairing.ui

import android.view.View
import androidx.camera.core.CameraSelector
import androidx.camera.core.CameraState
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LiveData
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.ReaderException
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader
import java.nio.ByteBuffer
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

// Main-thread owned. The one analysis worker only handles in-memory camera frames.
internal class PairingQrScanner(
    private val owner: LifecycleOwner,
    private val view: PreviewView,
    private val onOffer: (String) -> Unit,
    private val onInvalid: () -> Unit,
    private val onFailure: () -> Unit,
) : AutoCloseable {
    private val analyzerExecutor = Executors.newSingleThreadExecutor()
    private val closed = AtomicBoolean(false)
    private val preview = Preview.Builder().build()
    private val analysis = ImageAnalysis.Builder()
        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
    private var provider: ProcessCameraProvider? = null
    private var cameraState: LiveData<CameraState>? = null

    fun start() {
        val context = view.context
        val main = ContextCompat.getMainExecutor(context)
        val future = ProcessCameraProvider.getInstance(context)
        future.addListener({
            if (closed.get()) return@addListener
            try {
                val cameras = future.get()
                val selector = when {
                    cameras.hasCamera(CameraSelector.DEFAULT_BACK_CAMERA) -> CameraSelector.DEFAULT_BACK_CAMERA
                    cameras.hasCamera(CameraSelector.DEFAULT_FRONT_CAMERA) -> CameraSelector.DEFAULT_FRONT_CAMERA
                    else -> { onFailure(); return@addListener }
                }
                provider = cameras
                view.implementationMode = PreviewView.ImplementationMode.COMPATIBLE
                view.scaleType = PreviewView.ScaleType.FIT_CENTER
                view.importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
                preview.surfaceProvider = view.surfaceProvider
                val decoder = PairingQrDecoder()
                val reported = AtomicBoolean(false)
                var rejectedReported = false
                analysis.setAnalyzer(analyzerExecutor) { image ->
                    try {
                        if (!closed.get() && !reported.get()) {
                            decoder.decode(image)?.let { payload ->
                                if (payload.startsWith("storage-pair-v1:") && payload.length <= 259) {
                                    reported.set(true)
                                    main.execute { if (!closed.get()) onOffer(payload) }
                                } else if (!rejectedReported) {
                                    rejectedReported = true
                                    main.execute { if (!closed.get()) onInvalid() }
                                }
                            }
                        }
                    } finally {
                        image.close()
                    }
                }
                val camera = cameras.bindToLifecycle(owner, selector, preview, analysis)
                cameraState = camera.cameraInfo.cameraState.also { state ->
                    state.observe(owner) { if (!closed.get() && it.error != null) onFailure() }
                }
            } catch (_: Exception) {
                if (!closed.get()) onFailure()
            }
        }, main)
    }

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        cameraState?.removeObservers(owner)
        analysis.clearAnalyzer()
        provider?.unbind(preview, analysis)
        analyzerExecutor.shutdown()
    }
}

internal class PairingQrDecoder {
    private val reader = QRCodeReader()
    private var luminance = ByteArray(0)

    fun decode(image: ImageProxy): String? {
        val plane = image.planes.firstOrNull() ?: return null
        return decode(plane.buffer, image.width, image.height, plane.rowStride, plane.pixelStride)
    }

    fun decode(buffer: ByteBuffer, width: Int, height: Int, rowStride: Int, pixelStride: Int): String? {
        if (width <= 0 || height <= 0 || rowStride <= 0 || pixelStride <= 0 ||
            width.toLong() * height > 4_194_304L) return null
        val rowBytes = (width - 1L) * pixelStride + 1
        val source = buffer.duplicate()
        val offset = source.position()
        val last = offset + (height - 1L) * rowStride + rowBytes
        if (rowBytes > rowStride || last > source.limit()) return null
        if (luminance.size != width * height) luminance = ByteArray(width * height)
        for (y in 0 until height) {
            val row = offset + y * rowStride
            if (pixelStride == 1) {
                source.position(row)
                source.get(luminance, y * width, width)
            } else {
                for (x in 0 until width) luminance[y * width + x] = source.get(row + x * pixelStride)
            }
        }
        val image = PlanarYUVLuminanceSource(luminance, width, height, 0, 0, width, height, false)
        return try {
            reader.decode(BinaryBitmap(HybridBinarizer(image)), hints).text
        } catch (_: ReaderException) {
            null
        }
    }

    private companion object {
        val hints = mapOf(DecodeHintType.TRY_HARDER to true)
    }
}
