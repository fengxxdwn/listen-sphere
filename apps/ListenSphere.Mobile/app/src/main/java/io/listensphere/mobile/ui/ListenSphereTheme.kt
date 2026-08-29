package io.listensphere.mobile.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val ListenSphereBackground = Color(0xFF0F131A)
val ListenSphereSurface = Color(0xFF171D26)
val ListenSphereRaised = Color(0xFF1C232E)
val ListenSphereStroke = Color(0xFF29313D)
val ListenSphereAccent = Color(0xFF4C78F0)
val ListenSphereSuccess = Color(0xFF45C392)
val ListenSphereWarning = Color(0xFFDDB25A)
val ListenSphereError = Color(0xFFE06C75)
val ListenSphereSecondary = Color(0xFF8E99AA)

private val colors = darkColorScheme(
    primary = ListenSphereAccent,
    onPrimary = Color.White,
    secondary = ListenSphereSuccess,
    background = ListenSphereBackground,
    onBackground = Color(0xFFE7ECF3),
    surface = ListenSphereSurface,
    surfaceVariant = ListenSphereRaised,
    onSurface = Color(0xFFE7ECF3),
    onSurfaceVariant = ListenSphereSecondary,
    outline = ListenSphereStroke,
    error = ListenSphereError,
)

@Composable
fun ListenSphereTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = colors, content = content)
}
