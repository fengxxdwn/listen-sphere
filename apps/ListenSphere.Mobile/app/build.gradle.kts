import javax.xml.parsers.DocumentBuilderFactory

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

val listenSphereVersionFile = rootProject.file("../../eng/ListenSphere.Version.props")
val listenSphereVersionDocument = DocumentBuilderFactory.newInstance()
    .newDocumentBuilder()
    .parse(listenSphereVersionFile)

fun listenSphereVersionProperty(name: String): String {
    val nodes = listenSphereVersionDocument.getElementsByTagName(name)
    check(nodes.length == 1) { "Missing or duplicate $name in $listenSphereVersionFile" }
    return nodes.item(0).textContent.trim()
}

android {
    namespace = "io.listensphere.mobile"
    compileSdk = 34

    defaultConfig {
        applicationId = "io.listensphere.mobile"
        minSdk = 29
        targetSdk = 34
        versionCode = listenSphereVersionProperty("ListenSphereAndroidVersionCode").toInt()
        versionName = listenSphereVersionProperty("ListenSphereProductVersion")

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        vectorDrawables.useSupportLibrary = true
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    sourceSets {
        getByName("test").resources.srcDir("../../../protocol/test-vectors")
    }

    packaging {
        resources.excludes += setOf("META-INF/AL2.0", "META-INF/LGPL2.1")
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.activity:activity-compose:1.9.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.2")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.2")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
    implementation("com.google.protobuf:protobuf-javalite:3.25.3")

    implementation(platform("androidx.compose:compose-bom:2024.06.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    debugImplementation("androidx.compose.ui:ui-tooling")

    testImplementation("junit:junit:4.13.2")
}
