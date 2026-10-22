plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.fy1ng.fy1switch"
    compileSdk = 36
    defaultConfig {
        applicationId = "com.fy1ng.fy1switch"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "1.0.0"
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures { compose = true }
    testOptions { unitTests.isReturnDefaultValues = false }
    lint { abortOnError = true }
}

kotlin { compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) } }

dependencies {
    implementation("org.bouncycastle:bcprov-jdk18on:1.83")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.3")
    implementation(platform("androidx.compose:compose-bom:2025.08.01"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}

tasks.withType<Test>().configureEach {
    if (name != "liveShortCodeTest") exclude("**/LiveShortCodeTest*")
    if (name != "liveWindowsTest") exclude("**/LiveWindowsIntegrationTest*")
}

afterEvaluate {
    val debugUnitTest = tasks.named<Test>("testDebugUnitTest").get()
    tasks.register<Test>("liveShortCodeTest") {
        group = "verification"
        testClassesDirs = debugUnitTest.testClassesDirs
        classpath = debugUnitTest.classpath
        dependsOn(debugUnitTest.taskDependencies.getDependencies(debugUnitTest))
        include("**/LiveShortCodeTest.class")
        useJUnit()
        doNotTrackState("One-shot fixture must never be cached.")
        maxParallelForks = 1
        doFirst {
            val fixture = providers.environmentVariable("FY1SWITCH_SHORT_CODE_FIXTURE").orNull
            if (fixture.isNullOrBlank()) throw GradleException("FY1SWITCH_SHORT_CODE_FIXTURE must name a temporary fixture JSON file.")
            environment("FY1SWITCH_SHORT_CODE_FIXTURE", fixture)
        }
    }
    tasks.register<Test>("liveWindowsTest") {
        group = "verification"
        description = "Runs the real loopback Windows HTTPS fixture supplied by FY1SWITCH_ANDROID_FIXTURE."
        testClassesDirs = debugUnitTest.testClassesDirs
        classpath = debugUnitTest.classpath
        dependsOn(debugUnitTest.taskDependencies.getDependencies(debugUnitTest))
        include("**/LiveWindowsIntegrationTest.class")
        useJUnit()
        filter { isFailOnNoMatchingTests = true }
        doNotTrackState("Requires a fresh one-time pairing fixture on every explicit invocation.")
        maxParallelForks = 1
        testLogging.showStandardStreams = false
        doFirst {
            val fixture = providers.environmentVariable("FY1SWITCH_ANDROID_FIXTURE").orNull
            if (fixture.isNullOrBlank()) throw GradleException("FY1SWITCH_ANDROID_FIXTURE must point to a temporary Windows HTTPS fixture JSON.")
            environment("FY1SWITCH_ANDROID_FIXTURE", fixture)
        }
    }
}
