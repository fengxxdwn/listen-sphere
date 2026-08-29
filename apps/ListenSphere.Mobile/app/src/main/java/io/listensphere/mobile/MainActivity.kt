package io.listensphere.mobile

import android.Manifest
import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.hardware.usb.UsbManager
import android.media.projection.MediaProjectionManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import io.listensphere.mobile.core.model.CaptureKind
import io.listensphere.mobile.core.model.ControllerEndpoint
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.model.BluetoothChannelMode
import io.listensphere.mobile.core.model.BluetoothStreamCodec
import io.listensphere.mobile.core.model.TransportMode
import io.listensphere.mobile.core.usb.AndroidUsbAccessoryManager
import io.listensphere.mobile.service.AudioStreamingService
import io.listensphere.mobile.ui.ListenSphereScreen
import io.listensphere.mobile.ui.MobileViewModel
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    companion object {
        private const val ACTION_USB_PERMISSION = "io.listensphere.mobile.USB_PERMISSION"
    }

    private val viewModel by viewModels<MobileViewModel>()
    private val usbAccessoryManager by lazy { AndroidUsbAccessoryManager(this) }
    private var pendingEndpoint: ControllerEndpoint? = null
    private var pendingBluetoothPeer: BluetoothPeer? = null
    private var pendingTransportMode: TransportMode = TransportMode.WIFI
    private var pendingCode: String = ""
    private var pendingKind: CaptureKind? = null
    private var pendingBluetoothCodec = BluetoothStreamCodec.IMA_ADPCM
    private var pendingBluetoothChannelMode = BluetoothChannelMode.STEREO
    private var pendingNativeUsb = false

    private val usbPermissionReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            if (intent?.action != ACTION_USB_PERMISSION) return
            viewModel.refreshUsbAccessoryState()
            if (intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false)) {
                continueCaptureStart()
            } else {
                clearPending()
            }
        }
    }

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { permissions ->
        val audioGranted = permissions[Manifest.permission.RECORD_AUDIO]
            ?: (ContextCompat.checkSelfPermission(
                this,
                Manifest.permission.RECORD_AUDIO,
            ) == PackageManager.PERMISSION_GRANTED)
        val bluetoothGranted = pendingTransportMode != TransportMode.BLUETOOTH ||
            Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            (permissions[Manifest.permission.BLUETOOTH_CONNECT]
                ?: (ContextCompat.checkSelfPermission(
                    this,
                    Manifest.permission.BLUETOOTH_CONNECT,
                ) == PackageManager.PERMISSION_GRANTED))
        if (audioGranted && bluetoothGranted) continueStart() else clearPending()
        viewModel.refreshRuntimeState()
    }

    private val projectionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { result ->
        if (result.resultCode == Activity.RESULT_OK && result.data != null) {
            launchService(result.resultCode, result.data)
        } else {
            clearPending()
        }
    }

    private val bluetoothPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) {
        viewModel.refreshBluetoothPeers()
        viewModel.refreshRuntimeState()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ContextCompat.registerReceiver(
            this,
            usbPermissionReceiver,
            IntentFilter(ACTION_USB_PERMISSION),
            ContextCompat.RECEIVER_NOT_EXPORTED,
        )
        viewModel.refreshUsbAccessoryState()
        setContent {
            ListenSphereScreen(
                viewModel = viewModel,
                onStart = ::requestStart,
                onStop = {
                    startService(AudioStreamingService.stopIntent(this))
                },
                onBluetoothProbe = ::requestBluetoothProbe,
                onOpenAppSettings = ::openAppSettings,
                onOpenBatterySettings = ::openBatterySettings,
            )
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        viewModel.refreshUsbAccessoryState()
    }

    override fun onResume() {
        super.onResume()
        viewModel.refreshRuntimeState()
    }

    override fun onDestroy() {
        unregisterReceiver(usbPermissionReceiver)
        super.onDestroy()
    }

    private fun openAppSettings() {
        startActivity(
            Intent(
                Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                Uri.parse("package:$packageName"),
            ),
        )
    }

    private fun openBatterySettings() {
        startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
    }

    private fun requestBluetoothProbe() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(
                this,
                Manifest.permission.BLUETOOTH_CONNECT,
            ) != PackageManager.PERMISSION_GRANTED
        ) {
            bluetoothPermissionLauncher.launch(Manifest.permission.BLUETOOTH_CONNECT)
            return
        }
        lifecycleScope.launch { viewModel.probeSelectedBluetooth() }
    }

    private fun requestStart(kind: CaptureKind, pairingCodeOverride: String?) {
        lifecycleScope.launch {
            val mode = viewModel.transportMode.value
            pendingTransportMode = mode
            pendingNativeUsb = mode == TransportMode.USB && !viewModel.usbCompatibilityMode.value
            when (mode) {
                TransportMode.WIFI -> {
                    pendingEndpoint = runCatching { viewModel.resolveEndpoint() }.getOrNull()
                        ?: return@launch
                    pendingBluetoothPeer = null
                }
                TransportMode.USB -> {
                    pendingEndpoint = if (pendingNativeUsb) null else {
                        runCatching { viewModel.resolveEndpoint() }.getOrNull() ?: return@launch
                    }
                    pendingBluetoothPeer = null
                    if (pendingNativeUsb && usbAccessoryManager.selectedAccessory() == null) {
                        viewModel.refreshUsbAccessoryState()
                        return@launch
                    }
                }
                TransportMode.BLUETOOTH -> {
                    pendingBluetoothPeer = viewModel.selectedBluetoothPeer() ?: return@launch
                    pendingEndpoint = null
                }
            }
            pendingCode = pairingCodeOverride.orEmpty()
            pendingBluetoothCodec = viewModel.selectedBluetoothCodec.value
            pendingBluetoothChannelMode = viewModel.selectedBluetoothChannelMode.value
            pendingKind = kind
            val permissions = buildList {
                if (ContextCompat.checkSelfPermission(
                        this@MainActivity,
                        Manifest.permission.RECORD_AUDIO,
                    ) != PackageManager.PERMISSION_GRANTED
                ) {
                    add(Manifest.permission.RECORD_AUDIO)
                }
                if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(
                        this@MainActivity,
                        Manifest.permission.POST_NOTIFICATIONS,
                    ) != PackageManager.PERMISSION_GRANTED
                ) {
                    add(Manifest.permission.POST_NOTIFICATIONS)
                }
                if (mode == TransportMode.BLUETOOTH &&
                    Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
                    ContextCompat.checkSelfPermission(
                        this@MainActivity,
                        Manifest.permission.BLUETOOTH_CONNECT,
                    ) != PackageManager.PERMISSION_GRANTED
                ) {
                    add(Manifest.permission.BLUETOOTH_CONNECT)
                }
            }
            if (permissions.isEmpty()) continueStart() else permissionLauncher.launch(permissions.toTypedArray())
        }
    }

    private fun continueStart() {
        if (pendingNativeUsb) {
            val accessory = usbAccessoryManager.selectedAccessory() ?: run {
                viewModel.refreshUsbAccessoryState()
                clearPending()
                return
            }
            if (!getSystemService(UsbManager::class.java).hasPermission(accessory)) {
                usbAccessoryManager.requestPermission(accessory, ACTION_USB_PERMISSION)
                return
            }
        }
        continueCaptureStart()
    }

    private fun continueCaptureStart() {
        when (pendingKind) {
            CaptureKind.MICROPHONE -> launchService()
            CaptureKind.DEVICE_PLAYBACK -> {
                val manager = getSystemService(MediaProjectionManager::class.java)
                projectionLauncher.launch(manager.createScreenCaptureIntent())
            }
            null -> Unit
        }
    }

    private fun launchService(projectionResult: Int? = null, projectionData: Intent? = null) {
        val kind = pendingKind ?: return
        val intent = Intent(this, AudioStreamingService::class.java)
            .setAction(AudioStreamingService.ACTION_START)
            .putExtra(AudioStreamingService.EXTRA_TRANSPORT_MODE, pendingTransportMode.name)
            .putExtra(AudioStreamingService.EXTRA_PAIRING_CODE, pendingCode)
            .putExtra(AudioStreamingService.EXTRA_CAPTURE_KIND, kind.name)
            .putExtra(AudioStreamingService.EXTRA_USB_NATIVE, pendingNativeUsb)
        if (pendingTransportMode == TransportMode.WIFI ||
            (pendingTransportMode == TransportMode.USB && !pendingNativeUsb)) {
            val endpoint = pendingEndpoint ?: return
            intent.putExtra(AudioStreamingService.EXTRA_DEVICE_ID, endpoint.deviceId?.toString())
                .putExtra(AudioStreamingService.EXTRA_CONTROLLER_NAME, endpoint.displayName)
                .putExtra(AudioStreamingService.EXTRA_HOST, endpoint.address.hostAddress)
                .putExtra(AudioStreamingService.EXTRA_PORT, endpoint.port)
        } else {
            val peer = pendingBluetoothPeer ?: return
            intent.putExtra(AudioStreamingService.EXTRA_CONTROLLER_NAME, peer.displayName)
                .putExtra(AudioStreamingService.EXTRA_BLUETOOTH_ADDRESS, peer.address)
                .putExtra(AudioStreamingService.EXTRA_BLUETOOTH_CODEC, pendingBluetoothCodec.name)
                .putExtra(
                    AudioStreamingService.EXTRA_BLUETOOTH_CHANNEL_MODE,
                    pendingBluetoothChannelMode.name,
                )
        }
        if (projectionResult != null && projectionData != null) {
            intent.putExtra(AudioStreamingService.EXTRA_PROJECTION_RESULT, projectionResult)
            intent.putExtra(AudioStreamingService.EXTRA_PROJECTION_DATA, projectionData)
        }
        ContextCompat.startForegroundService(this, intent)
        clearPending()
    }

    private fun clearPending() {
        pendingEndpoint = null
        pendingBluetoothPeer = null
        pendingTransportMode = TransportMode.WIFI
        pendingCode = ""
        pendingKind = null
        pendingBluetoothCodec = BluetoothStreamCodec.IMA_ADPCM
        pendingBluetoothChannelMode = BluetoothChannelMode.STEREO
        pendingNativeUsb = false
    }
}
