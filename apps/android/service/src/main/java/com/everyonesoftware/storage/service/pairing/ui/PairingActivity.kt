package com.everyonesoftware.storage.service.pairing.ui

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.text.InputFilter
import android.text.InputType
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import com.everyonesoftware.storage.service.R
import com.everyonesoftware.storage.service.pairing.AndroidPairing
import com.everyonesoftware.storage.service.pairing.PairingCancellation
import com.everyonesoftware.storage.service.pairing.PairingChallenge
import com.everyonesoftware.storage.service.pairing.PairingDeclined
import com.everyonesoftware.storage.service.pairing.PairingPeer
import com.everyonesoftware.storage.service.pairing.PairingTarget
import com.everyonesoftware.storage.service.pairing.PairingUncertain
import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class PairingActivity : ComponentActivity() {
    private enum class Page { DEVICES, MANUAL, SCAN, JOIN }

    private lateinit var backend: PairingBackend
    private lateinit var content: LinearLayout
    private var page = Page.DEVICES
    private var generation = 0
    private var scanner: PairingQrScanner? = null
    private var attempt: Attempt? = null
    private var resumeNotice: Int? = null
    private var visibleNotice: Int? = null

    private val requestCamera = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (page != Page.DEVICES || isFinishing) return@registerForActivityResult
        if (granted) scan() else devices(R.string.pairing_camera_denied)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        backend = backendFactory(applicationContext)
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                when (page) {
                    Page.DEVICES -> finish()
                    Page.JOIN -> cancelJoin()
                    else -> devices()
                }
            }
        })
        // This exported launcher never consumes intent data, extras or restored secrets.
        devices(if (savedInstanceState?.getBoolean(UNCERTAIN) == true) R.string.pairing_uncertain else null)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        // Preserve only the warning, never the invitation, decision code or connection.
        outState.putBoolean(UNCERTAIN, attempt?.approved == true || resumeNotice == R.string.pairing_uncertain ||
            visibleNotice == R.string.pairing_uncertain)
    }

    override fun onResume() {
        super.onResume()
        resumeNotice?.let { notice ->
            resumeNotice = null
            devices(notice)
        }
    }

    override fun onPause() {
        if (page == Page.SCAN) {
            stopScanner()
            resumeNotice = R.string.pairing_cancelled
        }
        attempt?.let {
            resumeNotice = cancelAttempt(it)
        }
        super.onPause()
    }

    override fun onDestroy() {
        stopScanner()
        attempt?.let(::cancelAttempt)
        generation++
        super.onDestroy()
    }

    private fun devices(notice: Int? = null) {
        layout(Page.DEVICES, R.string.pairing_devices)
        visibleNotice = notice
        text(getString(R.string.pairing_intro))
        notice?.let { status(it) }
        val paired = text(getString(R.string.pairing_loading)).apply {
            accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        val supported = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
        if (!supported) status(R.string.pairing_requires_android_10)
        button(R.string.pairing_scan, ::requestScan).isEnabled = supported
        button(R.string.pairing_manual, ::manual).isEnabled = supported
        button(R.string.pairing_refresh) { devices() }
        val request = ++generation
        io.execute {
            val peers = try { backend.list() } catch (_: Exception) { null }
            runOnUiThread {
                if (isDestroyed || page != Page.DEVICES || request != generation) return@runOnUiThread
                paired.text = when {
                    peers == null -> getString(R.string.pairing_list_failed)
                    peers.isEmpty() -> getString(R.string.pairing_no_devices)
                    else -> peers.joinToString("\n") { getString(R.string.pairing_device, it.name) }
                }
            }
        }
    }

    private fun requestScan() {
        if (!packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_ANY)) {
            devices(R.string.pairing_camera_unavailable)
        } else if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            scan()
        } else {
            requestCamera.launch(Manifest.permission.CAMERA)
        }
    }

    private fun scan() {
        layout(Page.SCAN, R.string.pairing_scan)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        text(getString(R.string.pairing_scan_instructions))
        val preview = PreviewView(this)
        content.addView(preview, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(240)))
        val rejected = text("").apply {
            accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
            visibility = View.GONE
        }
        button(R.string.pairing_manual, ::manual)
        button(R.string.pairing_cancel) { devices() }
        val camera = PairingQrScanner(this, preview, onOffer = { connect(it, fromCamera = true) }, onInvalid = {
            rejected.setText(R.string.pairing_not_offer)
            rejected.visibility = View.VISIBLE
        }, onFailure = { devices(R.string.pairing_camera_unavailable) })
        scanner = camera
        camera.start()
    }

    private fun manual() {
        layout(Page.MANUAL, R.string.pairing_manual_title)
        text(getString(R.string.pairing_manual_instructions))
        val field = EditText(this).apply {
            setHint(R.string.pairing_address)
            isSingleLine = true
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            imeOptions = EditorInfo.IME_ACTION_GO or EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING
            filters = arrayOf(InputFilter.LengthFilter(259))
            isSaveEnabled = false
        }
        content.addView(field, LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
        fun submit() {
            val input = field.text.toString().trim()
            // Manual mode accepts only an address, never a scanned offer pasted into the field.
            val valid = !input.startsWith("storage-pair-", ignoreCase = true) && try {
                PairingTarget.parse(input, System.currentTimeMillis() / 1000).offer == null
            } catch (_: IOException) { false }
            if (!valid) {
                field.error = getString(R.string.pairing_address_error)
                return
            }
            getSystemService(InputMethodManager::class.java).hideSoftInputFromWindow(field.windowToken, 0)
            connect(input, fromCamera = false)
        }
        field.setOnEditorActionListener { _, id, _ ->
            if (id == EditorInfo.IME_ACTION_GO) { submit(); true } else false
        }
        button(R.string.pairing_connect, ::submit)
        button(R.string.pairing_cancel) { devices() }
    }

    private fun connect(input: String, fromCamera: Boolean) {
        if (attempt != null || (if (fromCamera) page != Page.SCAN else page != Page.MANUAL)) return
        val session = Attempt()
        attempt = session
        layout(Page.JOIN, R.string.pairing_connecting)
        button(R.string.pairing_cancel, ::cancelJoin)
        io.execute {
            val message = try {
                if (!session.decision.isDone) {
                    backend.join(input, { challenge ->
                        runOnUiThread {
                            if (attempt === session && !session.decision.isDone) compare(session, challenge)
                        }
                        session.decision.get(90, TimeUnit.SECONDS)
                    }, session.cancellation)
                }
                R.string.pairing_success
            } catch (_: PairingDeclined) {
                R.string.pairing_declined
            } catch (_: PairingUncertain) {
                R.string.pairing_uncertain
            } catch (_: Exception) {
                if (fromCamera) R.string.pairing_failed_scan else R.string.pairing_failed_manual
            }
            runOnUiThread {
                if (attempt !== session || isDestroyed) return@runOnUiThread
                attempt = null
                devices(message)
            }
        }
    }

    private fun compare(session: Attempt, challenge: PairingChallenge) {
        layout(Page.JOIN, R.string.pairing_confirm)
        text(getString(R.string.pairing_confirm_instructions, challenge.peerName))
        val spacedCode = challenge.code.toList().joinToString(" ")
        text(spacedCode, size = 36f).apply {
            typeface = Typeface.MONOSPACE
            textDirection = View.TEXT_DIRECTION_LTR
            contentDescription = getString(R.string.pairing_accessible_code, spacedCode)
        }
        button(R.string.pairing_match) {
            if (attempt === session && session.decision.complete(true)) {
                session.approved = true
                layout(Page.JOIN, R.string.pairing_waiting)
                button(R.string.pairing_cancel, ::cancelJoin)
            }
        }
        button(R.string.pairing_mismatch) {
            if (attempt === session && session.decision.complete(false)) {
                // Let the wire send its rejection before the socket is closed.
                layout(Page.JOIN, R.string.pairing_waiting)
                button(R.string.pairing_cancel, ::cancelJoin)
            }
        }
        button(R.string.pairing_cancel, ::cancelJoin)
    }

    private fun cancelJoin() {
        attempt?.let { devices(cancelAttempt(it)) }
    }

    private fun cancelAttempt(session: Attempt): Int {
        attempt = null
        session.cancellation.cancel()
        session.decision.complete(false)
        return if (session.approved) R.string.pairing_uncertain else R.string.pairing_cancelled
    }

    private fun stopScanner() {
        scanner?.close()
        scanner = null
    }

    private fun layout(next: Page, title: Int) {
        stopScanner()
        page = next
        visibleNotice = null
        generation++
        content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(24), dp(20), dp(24), dp(24))
            isFocusableInTouchMode = true
        }
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            addView(content)
        }
        ViewCompat.setOnApplyWindowInsetsListener(scroll) { view, insets ->
            val safe = insets.getInsets(WindowInsetsCompat.Type.systemBars() or
                WindowInsetsCompat.Type.displayCutout() or WindowInsetsCompat.Type.ime())
            view.setPadding(safe.left, safe.top, safe.right, safe.bottom)
            insets
        }
        setContentView(scroll)
        if (next != Page.SCAN) window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
        content.requestFocus()
        text(getString(R.string.pairing_app_name), size = 18f).setTypeface(null, Typeface.BOLD)
        text(getString(title), size = 26f).apply { ViewCompat.setAccessibilityHeading(this, true) }
        ViewCompat.setAccessibilityPaneTitle(content, getString(title))
    }

    private fun text(value: String, size: Float = 16f) = TextView(this).apply {
        text = value
        textSize = size
        setPadding(0, 0, 0, dp(12))
        content.addView(this)
    }

    private fun status(res: Int) = text(getString(res)).apply {
        accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
    }

    private fun button(res: Int, action: () -> Unit) = Button(this).apply {
        setText(res)
        isAllCaps = false
        minHeight = dp(48)
        setOnClickListener { action() }
        content.addView(this, LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density + .5f).toInt()

    private class Attempt {
        val cancellation = PairingCancellation()
        val decision = CompletableFuture<Boolean>()
        var approved = false
    }

    internal companion object {
        private const val UNCERTAIN = "pairingUnconfirmed"
        private val io = Executors.newSingleThreadExecutor()
        internal val productionBackend: (Context) -> PairingBackend = { context ->
            val pairing = AndroidPairing(context)
            PairingBackend(pairing::list, pairing::join)
        }
        // Instrumentation in the same process can replace only the worker, not the UI.
        @Volatile internal var backendFactory = productionBackend
    }
}

internal class PairingBackend(
    val list: () -> List<PairingPeer>,
    val join: (String, (PairingChallenge) -> Boolean, PairingCancellation) -> PairingPeer,
)
