const UGC_API_BASE = 'https://public.facepunch.com/sbox/package'

export class UgcApiError extends Error {
  status: number
  constructor(status: number, statusText: string) {
    super(`UGC API error: ${status} ${statusText}`)
    this.name = 'UgcApiError'
    this.status = status
  }
}

export const PACKAGE_TYPES = [
  'model', 'sound', 'material', 'map', 'game',
  'clothing', 'library', 'vsnd', 'prefab', 'shader',
] as const

export type PackageType = (typeof PACKAGE_TYPES)[number]

export interface PackageOrg {
  Ident: string
  Title: string
  Description?: string
  Thumb?: string
  Twitter?: string
  WebUrl?: string
  Discord?: string
}

export interface UgcPackage {
  Org: PackageOrg
  Ident: string
  FullIdent: string
  Title: string
  Summary?: string
  Thumb?: string
  ThumbWide?: string
  ThumbTall?: string
  VideoThumb?: string
  TypeName: string
  Updated: string
  Created: string
  UsageStats?: Record<string, unknown>
  Tags?: string[]
  Favourited?: number
  VotesUp?: number
  Collections?: number
  Referenced?: number
  Public?: boolean
}

export interface SearchPackagesResponse {
  Packages: UgcPackage[]
  TotalCount: number
  Facets: unknown[]
  Tags?: Record<string, number>
  Orders?: Array<{ Name: string; Title: string; Icon: string }>
  Properties?: unknown[]
}

export interface SearchPackagesParams {
  query: string
  type?: PackageType
  category?: string
  tag?: string
  order?: string
  take?: number
  skip?: number
}

// Detail DTO returned by /get/2/{ident}. Unlike search results, it has no FullIdent
// (the ident is already known by the caller) and Org is trimmed to Ident/Title/Thumb.
export interface PackageDetail {
  Org: PackageOrg
  Ident: string
  Title: string
  Summary?: string
  Thumb?: string
  ThumbWide?: string
  ThumbTall?: string
  TypeName: string
  Updated: string
  Created: string
  UsageStats?: Record<string, unknown>
  Tags?: string[]
  Favourited?: number
  VotesUp?: number
  Public?: boolean
}

export async function searchPackages(params: SearchPackagesParams): Promise<SearchPackagesResponse> {
  const url = new URL(`${UGC_API_BASE}/find/2`)

  url.searchParams.set('q', params.query)
  // type/category/tag/order predate find/2 and are empirically no-ops there (verified:
  // identical results with and without them), but they're harmless so we keep passing them.
  if (params.type) url.searchParams.set('type', params.type)
  if (params.category) url.searchParams.set('category', params.category)
  if (params.tag) url.searchParams.set('tag', params.tag)
  if (params.order) url.searchParams.set('order', params.order)
  if (params.take) url.searchParams.set('take', String(params.take))
  if (params.skip) url.searchParams.set('skip', String(params.skip))

  const res = await fetch(url.toString())
  if (!res.ok) {
    throw new UgcApiError(res.status, res.statusText)
  }

  return res.json() as Promise<SearchPackagesResponse>
}

export async function getPackage(ident: string): Promise<PackageDetail> {
  const url = `${UGC_API_BASE}/get/2/${encodeURIComponent(ident)}`

  const res = await fetch(url)
  if (!res.ok) {
    throw new UgcApiError(res.status, res.statusText)
  }

  return res.json() as Promise<PackageDetail>
}
