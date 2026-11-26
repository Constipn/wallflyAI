import Foundation

struct SummaryModel: Identifiable, Equatable {
    let id: String
    let name: String
}

/// Curated text models, checked against the official provider catalogs on 2026-10-29.
/// Existing/manual IDs remain selectable; availability depends on the user's API account.
enum SummaryModels {
    static func choices(for provider: EnhancementProvider) -> [SummaryModel] {
        switch provider {
        case .anthropic:
            return [.init(id: "claude-sonnet-5-5", name: "Claude Sonnet 5.5"),
                    .init(id: "claude-haiku-5-5", name: "Claude Haiku 5.5"),
                    .init(id: "claude-opus-5-5", name: "Claude Opus 5.5"),
                    .init(id: "claude-fable-5-1", name: "Claude Fable 5.1")]
        case .openai:
            return [.init(id: "gpt-6-luna", name: "GPT-6 Luna"),
                    .init(id: "gpt-6.1-terra", name: "GPT-6.1 Terra"),
                    .init(id: "gpt-6.1-sol", name: "GPT-6.1 Sol"),
                    .init(id: "gpt-6-astra", name: "GPT-6 Astra")]
        default: return []
        }
    }
    static func defaultModel(for provider: EnhancementProvider) -> String {
        switch provider {
        case .compatible: return ""
        default: return choices(for: provider).first?.id ?? ""
        }
    }
}
