package io.listensphere.mobile.ui

import android.content.Context
import io.listensphere.mobile.core.discovery.AndroidBluetoothDiscovery
import io.listensphere.mobile.core.discovery.AndroidControllerDiscovery
import io.listensphere.mobile.core.model.BluetoothPeer
import io.listensphere.mobile.core.network.BluetoothRfcommProbeClient
import io.listensphere.mobile.core.usb.AndroidUsbAccessoryManager

/** Owns discovery/probing resources; MobileViewModel projects results into UI state. */
internal class MobileConnectionCoordinator(context: Context) : AutoCloseable {
    private val discovery = AndroidControllerDiscovery(context)
    private val bluetooth = AndroidBluetoothDiscovery(context)
    private val probe = BluetoothRfcommProbeClient(context)
    private val usb = AndroidUsbAccessoryManager(context)
    val controllers = discovery.controllers
    val bluetoothPeers = bluetooth.peers
    val bluetoothStatus = bluetooth.status
    fun start() { discovery.start(); bluetooth.refresh() }
    fun refreshDiscovery() = discovery.refresh()
    fun refreshBluetooth() = bluetooth.refresh()
    fun usbState() = usb.state()
    suspend fun probe(peer: BluetoothPeer) = probe.probe(peer)
    override fun close() = discovery.close()
}
