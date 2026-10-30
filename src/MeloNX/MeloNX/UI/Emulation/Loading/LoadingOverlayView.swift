//
//  LoadingOverlayView.swift
//  MeloNX
//
//  Created by Stossy11 on 07/11/2025.
//

import SwiftUI
import Combine

class LoadingOverlayViewModel: ObservableObject {
    @Published var isLoading = true
    @Published var isAnimating = false
    @Published var isShaderOrPTC = false
    @Published var loadingType = ""
    @Published var currentProgress = 0
    @Published var totalProgress = 1

    
    private var registered = false
    func setupLoading() {
        guard !registered else { return }
        registered = true
        CallbackManager.register(name: "ProgressWithPTCorShaderCache") { [weak self] callbackData in
            guard callbackData.len > 0, let ptr = callbackData.ptr,
                  let array = try? JSONSerialization.jsonObject(with: Data(bytes: ptr, count: Int(callbackData.len))) as? [Any],
                  array.count == 3, let type = array[0] as? String,
                  let current = array[1] as? Int, let total = array[2] as? Int else {
                DispatchQueue.main.async { [weak self] in self?.isShaderOrPTC = false }
                return
            }
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.isShaderOrPTC = current < total - 10
                self.loadingType = type
                self.currentProgress = current
                self.totalProgress = max(1, total)
            }
        }
    }

    func stopLoading() {
        guard registered else { return }
        registered = false
        CallbackManager.unregister(name: "ProgressWithPTCorShaderCache")
    }

}

struct LoadingOverlayView: View {
    let game: GameInfo?
    let showLogs: Bool
    
    @StateObject private var vm = LoadingOverlayViewModel()
    @State private var contentVisible = false
    
    private let clumpWidth: CGFloat = 80
    
    var body: some View {
        if vm.isLoading {
            ZStack {
                Rectangle()
                    .fill(.ultraThinMaterial)
                    .ignoresSafeArea()
                
                Color.black.opacity(0.45)
                    .ignoresSafeArea()
                
                GeometryReader { geo in
                    loadingContent(geo: geo)
                }
            }
            .opacity(contentVisible ? 1 : 0)
            .onDisappear { vm.stopLoading() }
            .onAppear {
                vm.setupLoading()
                vm.isAnimating = true
                withAnimation(.easeIn(duration: 0.3)) {
                    contentVisible = true
                }
            }
        }
    }
    
    @ViewBuilder
    private func loadingContent(geo: GeometryProxy) -> some View {
        HStack(spacing: geo.size.width * 0.04) {
            if let icon = game?.icon {
                Image(uiImage: icon)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(
                        width: min(geo.size.width * 0.22, 220),
                        height: min(geo.size.width * 0.22, 220)
                    )
                    .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
                    .shadow(color: .black.opacity(0.6), radius: 30, x: 0, y: 12)
            }
            
            VStack(alignment: .leading, spacing: geo.size.height * 0.018) {
                Text("Loading \(game?.titleName ?? "Game")")
                    .font(.system(
                        size: min(geo.size.width * 0.038, 28),
                        weight: .semibold,
                        design: .rounded
                    ))
                    .foregroundStyle(.white)
                
                LoadingProgressBar(
                    screenGeometry: geo,
                    isAnimating: $vm.isAnimating,
                    isShaderOrPTC: $vm.isShaderOrPTC,
                    currentProgress: $vm.currentProgress,
                    totalProgress: $vm.totalProgress,
                    clumpWidth: clumpWidth
                )
                
                if vm.isShaderOrPTC {
                    progressLabel(geo: geo)
                }
            }
        }
        .padding(.horizontal, geo.size.width * 0.06)
        .padding(.vertical, geo.size.height * 0.05)
        .position(
            x: geo.size.width / 2,
            y: geo.size.height / 2
        )
    }
    
    private func progressLabel(geo: GeometryProxy) -> some View {
        HStack(spacing: 4) {
            Text(vm.loadingType + ": ")
                .fontWeight(.medium)
            Text("\(vm.currentProgress)")
                .monospacedDigit()
            Text("/")
                .opacity(0.5)
            Text("\(vm.totalProgress)")
                .monospacedDigit()
        }
        .font(.system(size: min(geo.size.width * 0.025, 13)))
        .foregroundStyle(.white.opacity(0.55))
    }
}
