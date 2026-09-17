using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Document
{
    // Root myDeserializedClass = JsonConvert.DeserializeObject<Root>(myJsonResponse);

    /// <summary>
    /// Invoice v1.0
    /// https://sdk.myinvois.hasil.gov.my/documents/invoice-v1-0/
    /// </summary>
    public class Root
    {
        public string _D { get; set; }
        public string _A { get; set; }
        public string _B { get; set; }
        public string _E { get; set; }
        public List<InvoiceJson> Invoice { get; set; }
    }

    /// <summary>
    /// TODO
    /// missing  / ubl:Invoice / cac:Signature
    /// Issuer’s Digital Signature
    /// </summary>
    public class InvoiceJson
    {
        public List<UBLExtensionForSign> UBLExtensions { get; set; }
        public List<UBLVersionID> UBLVersionID { get; set; }
        public List<ID> ID { get; set; }
        public List<IssueDate> IssueDate { get; set; }
        public List<IssueTime> IssueTime { get; set; }
        public List<InvoiceTypeCode> InvoiceTypeCode { get; set; }
        public List<DocumentCurrencyCode> DocumentCurrencyCode { get; set; }
        public List<DocumentCurrencyCode> TaxCurrencyCode { get; set; }
        public List<TaxExchangeRate> TaxExchangeRate { get; set; }
        public List<SignatureInv> Signature { get; set; }
        public List<InvoicePeriod> InvoicePeriod { get; set; }
        //for credit note
        public List<BillingReference> BillingReference { get; set; }
        public List<AdditionalDocumentReference> AdditionalDocumentReference { get; set; }

        public List<AccountingSupplierParty> AccountingSupplierParty { get; set; }
        public List<AccountingCustomerParty> AccountingCustomerParty { get; set; }
        public List<Delivery> Delivery { get; set; }
        public List<PaymentMean> PaymentMeans { get; set; }
        public List<PaymentTerm> PaymentTerms { get; set; }
        public List<PrepaidPayment> PrepaidPayment { get; set; }
        public List<AllowanceCharge> AllowanceCharge { get; set; }
        public List<TaxTotal> TaxTotal { get; set; }
        public List<LegalMonetaryTotal> LegalMonetaryTotal { get; set; }
        public List<InvoiceLine> InvoiceLine { get; set; }

        public List<Note> Note { get; set; }
        public List<TaxPointDate> TaxPointDate { get; set; }

        public List<AccountingCost> AccountingCost { get; set; }

        public List<OrderReference> OrderReference { get; set; }
        public List<ContractDocumentReference> ContractDocumentReference { get; set; }

        public List<PayeeParty> PayeeParty { get; set; }





    }

    public class SignatureInv
    {
        public List<ID> ID { get; set; }
        public List<SignatureMethodInv> SignatureMethod { get; set; }
        public List<SignatoryParty> SignatoryParty { get; set; }

    }
    public class SignatoryParty
    {
        public List<PartyIdentification> PartyIdentification { get; set; }
    }

    public class SignatureMethodInv
    {
        public string _ { get; set; }
    }

    public class InvoiceDocumentReference
    {

        public List<ID> ID { get; set; }
        public List<ID> UUID { get; set; }
        public List<IssueDate> IssueDate { get; set; }

    }


    public class AccountingCost
    {
        public string _ { get; set; }
    }

    public class AccountingCustomerParty
    {
        public List<Party> Party { get; set; }
    }

    public class AccountingSupplierParty
    {
        public List<Party> Party { get; set; }
        public List<AdditionalAccountID> AdditionalAccountID { get; set; }
    }

    public class AdditionalAccountID
    {
        public string _ { get; set; }
        public string schemeAgencyName { get; set; }
    }

    public class ActualDeliveryDate
    {
        public string _ { get; set; }
    }

    public class AdditionalDocumentReference
    {
        public List<ID> ID { get; set; }
        public List<DocumentType> DocumentType { get; set; }
        public List<Attachment> Attachment { get; set; }
        public List<DocumentDescription> DocumentDescription { get; set; }
    }

    public class AdditionalItemProperty
    {
        public List<Name> Name { get; set; }
        public List<Value> Value { get; set; }
    }

    public class AdditionalStreetName
    {
        public string _ { get; set; }
    }

    public class Address
    {
        public List<StreetName> StreetName { get; set; }
        public List<AdditionalStreetName> AdditionalStreetName { get; set; }
        public List<BuildingNumber> BuildingNumber { get; set; }
        public List<CityName> CityName { get; set; }
        public List<PostalZone> PostalZone { get; set; }
        public List<CountrySubentity> CountrySubentity { get; set; }
        public List<Country> Country { get; set; }
    }

    public class AddressLine
    {
        public List<Line> Line { get; set; }
    }
    public class AllowanceCharge
    {
        public List<ChargeIndicator> ChargeIndicator { get; set; }
        public List<AllowanceChargeReason> AllowanceChargeReason { get; set; }
        public List<Amount> Amount { get; set; }
        public List<MultiplierFactorNumeric> MultiplierFactorNumeric { get; set; }
        public List<BaseAmount> BaseAmount { get; set; }
    }

    public class AllowanceChargeReason
    {
        public string _ { get; set; }
    }

    public class AllowanceTotalAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class Amount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class Attachment
    {
        public List<ExternalReference> ExternalReference { get; set; }
        public List<EmbeddedDocumentBinaryObject> EmbeddedDocumentBinaryObject { get; set; }
    }

    public class BaseAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class BaseQuantity
    {
        public double _ { get; set; }
        public string unitCode { get; set; }
    }

    public class BillingReference
    {
        public List<InvoiceDocumentReference> InvoiceDocumentReference { get; set; }
        public List<AdditionalDocumentReference> AdditionalDocumentReference { get; set; }
    }
    public class BuildingNumber
    {
        public string _ { get; set; }
    }

    public class ChargeIndicator
    {
        public bool _ { get; set; }
    }

    public class ChargeTotalAmount
    {
        public int _ { get; set; }
        public string currencyID { get; set; }
    }

    public class CityName
    {
        public string _ { get; set; }
    }

    public class ClassifiedTaxCategory
    {
        public List<ID> ID { get; set; }
        public List<Percent> Percent { get; set; }
        public List<TaxScheme> TaxScheme { get; set; }
    }

    public class CommodityClassification
    {
        public List<ItemClassificationCode> ItemClassificationCode { get; set; }
    }

    public class CompanyID
    {
        public string _ { get; set; }
        public string schemeID { get; set; }
        public string IdentificationSchemeAgencyIdentifier { get; set; }
    }

    public class Contact
    {
        public List<Telephone> Telephone { get; set; }
        public List<Telefax> Telefax { get; set; }
        public List<ElectronicMail> ElectronicMail { get; set; }
    }

    public class ContractDocumentReference
    {
        public List<ID> ID { get; set; }
        public List<DocumentType> DocumentType { get; set; }
    }

    public class Country
    {
        public List<IdentificationCode> IdentificationCode { get; set; }
    }

    public class CountrySubentity
    {
        public string _ { get; set; }
    }

    public class CountrySubentityCode
    {
        public string _ { get; set; }
    }

    public class Delivery
    {
        public List<DeliveryParty> DeliveryParty { get; set; }
        public List<Shipment> Shipment { get; set; }
    }

    public class DeliveryParty
    {
        public List<PartyLegalEntity> PartyLegalEntity { get; set; }
        public List<PostalAddress> PostalAddress { get; set; }
        public List<PartyIdentification> PartyIdentification { get; set; }
    }

    public class DeliveryLocation
    {
        public List<ID> ID { get; set; }
        public List<Address> Address { get; set; }
    }

    public class Department
    {
        public string _ { get; set; }
    }

    public class Description
    {
        public string _ { get; set; }
        public string LanguageIdentifier { get; set; }
    }

    public class DocumentCurrencyCode
    {
        public string _ { get; set; }
        public string CodeListIdentifier { get; set; }
        public string CodeListAgencyIdentifier { get; set; }
    }

    public class DocumentDescription
    {
        public string _ { get; set; }
    }

    public class DocumentType
    {
        public string _ { get; set; }
    }

    public class ElectronicMail
    {
        public string _ { get; set; }
    }

    public class EmbeddedDocumentBinaryObject
    {
        public string BinaryObjectContent { get; set; }
        public string BinaryObjectMimeCode { get; set; }
    }

    public class EndDate
    {
        public string _ { get; set; }
    }

    public class EndpointID
    {
        public string _ { get; set; }
        public string schemeID { get; set; }
        public string IdentificationSchemeAgencyIdentifier { get; set; }
    }

    public class ExternalReference
    {
        public List<URI> URI { get; set; }
    }

    public class FamilyName
    {
        public string _ { get; set; }
    }

    public class FinancialInstitution
    {
        public List<ID> ID { get; set; }
    }

    public class FinancialInstitutionBranch
    {
        public List<FinancialInstitution> FinancialInstitution { get; set; }
    }

    public class FirstName
    {
        public string _ { get; set; }
    }

    public class FreightAllowanceCharge
    {
        public List<ChargeIndicator> ChargeIndicator { get; set; }
        public List<AllowanceChargeReason> AllowanceChargeReason { get; set; }
        public List<Amount> Amount { get; set; }
    }

    public class ID
    {
        public string _ { get; set; }
        public string schemeID { get; set; }
        public string schemeAgencyID { get; set; }
    }

    public class ItemTaxSchema
    {

        public string _ { get; set; }

    }

    public class IdentificationCode
    {
        public string _ { get; set; }
        public string listID { get; set; }
        public string listAgencyID { get; set; }
    }

    public class IndustryClassificationCode
    {
        public string _ { get; set; }
        public string name { get; set; }

    }
    public class InvoicedQuantity
    {
        public double _ { get; set; } // suppose to be decimal
        public string unitCode { get; set; }
        public string ListId { get; set; }
    }

    public class InvoiceLine
    {
        //invoice line
        public List<ID> ID { get; set; }

        public List<InvoicedQuantity> InvoicedQuantity { get; set; }
        public List<LineExtensionAmount> LineExtensionAmount { get; set; }
        public List<AllowanceCharge> AllowanceCharge { get; set; }
        public List<TaxTotal> TaxTotal { get; set; }
        public List<Item> Item { get; set; }
        public List<Price> Price { get; set; }
        public List<ItemPriceExtension> ItemPriceExtension { get; set; }


        public List<AccountingCost> AccountingCost { get; set; }
        public List<OrderLineReference> OrderLineReference { get; set; }

        public List<Note> Note { get; set; }
    }

    public class ItemPriceExtension
    {
        public List<Amount> Amount { get; set; }
    }

    public class InvoicePeriod
    {
        public List<StartDate> StartDate { get; set; }
        public List<EndDate> EndDate { get; set; }
        public List<Description> Description { get; set; }
    }

    public class InvoiceTypeCode
    {
        public string _ { get; set; }
        public string listVersionID { get; set; }
        public string listAgencyID { get; set; }
    }

    public class IssueDate
    {
        public string _ { get; set; }
    }
    public class IssueTime
    {
        public string _ { get; set; }
    }

    public class Item
    {
        public List<CommodityClassification> CommodityClassification { get; set; }
        public List<Description> Description { get; set; }
        public List<OriginCountry> OriginCountry { get; set; }
        public List<Name> Name { get; set; }
        public List<SellersItemIdentification> SellersItemIdentification { get; set; }
        public List<StandardItemIdentification> StandardItemIdentification { get; set; }

        public List<ClassifiedTaxCategory> ClassifiedTaxCategory { get; set; }
        public List<AdditionalItemProperty> AdditionalItemProperty { get; set; }

        public List<ItemPriceExtension> ItemPriceExtension { get; set; }
    }

    public class ItemClassificationCode
    {
        public string _ { get; set; }
        public string listAgencyID { get; set; }
        public string listID { get; set; }
    }

    public class JobTitle
    {
        public string _ { get; set; }
    }

    public class LegalMonetaryTotal
    {
        public List<LineExtensionAmount> LineExtensionAmount { get; set; }
        public List<TaxExclusiveAmount> TaxExclusiveAmount { get; set; }
        public List<TaxInclusiveAmount> TaxInclusiveAmount { get; set; }
        public List<AllowanceTotalAmount> AllowanceTotalAmount { get; set; }
        public List<ChargeTotalAmount> ChargeTotalAmount { get; set; }
        public List<PrepaidAmount> PrepaidAmount { get; set; }
        public List<PayableRoundingAmount> PayableRoundingAmount { get; set; }
        public List<PayableAmount> PayableAmount { get; set; }
    }

    public class LineExtensionAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class ItemExtensionAmount
    {
        public LineExtensionAmount Amount { get; set; }
    }

    public class LineID
    {
        public string _ { get; set; }
    }

    public class Line
    {
        public string _ { get; set; }
    }
    public class MiddleName
    {
        public string _ { get; set; }
    }

    public class MultiplierFactorNumeric
    {
        public double _ { get; set; }
    }

    public class Name
    {
        public string _ { get; set; }
    }

    public class Note
    {
        public string _ { get; set; }
        public string LanguageIdentifier { get; set; }
    }

    public class OrderLineReference
    {
        public List<LineID> LineID { get; set; }
    }

    public class OrderReference
    {
        public List<ID> ID { get; set; }
    }

    public class OriginCountry
    {
        public List<IdentificationCode> IdentificationCode { get; set; }
    }

    public class PaidAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class PaidDate
    {
        public string _ { get; set; }
    }

    public class PaidTime
    {
        public string _ { get; set; }
    }
    public class Party
    {
        public List<IndustryClassificationCode> IndustryClassificationCode { get; set; }
        public List<PartyIdentification> PartyIdentification { get; set; }
        public List<PostalAddress> PostalAddress { get; set; }
        public List<PartyLegalEntity> PartyLegalEntity { get; set; }
        public List<Contact> Contact { get; set; }
        public List<PartyName> PartyName { get; set; }
        public List<PartyTaxScheme> PartyTaxScheme { get; set; }

        public List<Person> Person { get; set; }
        public List<EndpointID> EndpointID { get; set; }

    }

    public class PartyIdentification
    {
        public List<ID> ID { get; set; }
    }

    public class PartyLegalEntity
    {
        public List<RegistrationName> RegistrationName { get; set; }
        public List<CompanyID> CompanyID { get; set; }
        public List<RegistrationAddress> RegistrationAddress { get; set; }
    }

    //public class PartyIndustryClassificationCode
    //{
    //    public List<IndustryClassificationCode> IndustryClassificationCode { get; set; }
    //}

    public class PartyName
    {
        public List<Name> Name { get; set; }
    }

    public class PartyTaxScheme
    {
        public List<CompanyID> CompanyID { get; set; }
        public List<TaxScheme> TaxScheme { get; set; }
    }

    public class PayableAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class PayableRoundingAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class PayeeFinancialAccount
    {
        public List<ID> ID { get; set; }
        public List<FinancialInstitutionBranch> FinancialInstitutionBranch { get; set; }
    }

    public class PayeeParty
    {
        public List<PartyIdentification> PartyIdentification { get; set; }
        public List<PartyName> PartyName { get; set; }
        public List<PartyLegalEntity> PartyLegalEntity { get; set; }
    }

    public class PaymentChannelCode
    {
        public string _ { get; set; }
    }

    public class PaymentDueDate
    {
        public string _ { get; set; }
    }

    public class PaymentID
    {
        public string _ { get; set; }
    }

    public class PaymentMean
    {
        public List<PaymentMeansCode> PaymentMeansCode { get; set; }
        public List<PaymentDueDate> PaymentDueDate { get; set; }
        public List<PaymentChannelCode> PaymentChannelCode { get; set; }
        public List<PaymentID> PaymentID { get; set; }
        public List<PayeeFinancialAccount> PayeeFinancialAccount { get; set; }
    }

    public class PaymentMeansCode
    {
        public string _ { get; set; }
        public string CodeListIdentifier { get; set; }
    }

    public class PaymentTerm
    {
        public List<Note> Note { get; set; }
    }

    public class Percent
    {
        public double _ { get; set; }
    }

    public class Person
    {
        public List<FirstName> FirstName { get; set; }
        public List<FamilyName> FamilyName { get; set; }
        public List<MiddleName> MiddleName { get; set; }
        public List<JobTitle> JobTitle { get; set; }
    }

    public class PostalAddress
    {
        public List<CityName> CityName { get; set; }
        public List<PostalZone> PostalZone { get; set; }
        public List<CountrySubentityCode> CountrySubentityCode { get; set; }
        public List<AddressLine> AddressLine { get; set; }
        public List<Country> Country { get; set; }
        public List<Postbox> Postbox { get; set; }
        public List<StreetName> StreetName { get; set; }
        public List<AdditionalStreetName> AdditionalStreetName { get; set; }
        public List<BuildingNumber> BuildingNumber { get; set; }
        public List<Department> Department { get; set; }
        public List<ID> ID { get; set; }



        public List<CountrySubentity> CountrySubentity { get; set; }
    }

    public class PostalZone
    {
        public string _ { get; set; }
    }


    public class Postbox
    {
        public string _ { get; set; }
    }

    public class PrepaidAmount
    {
        public int _ { get; set; }
        public string currencyID { get; set; }
    }

    public class PrepaidPayment
    {
        public List<ID> ID { get; set; }
        public List<PaidAmount> PaidAmount { get; set; }
        public List<PaidDate> PaidDate { get; set; }
        public List<PaidTime> PaidTime { get; set; }
    }

    public class Price
    {
        public List<PriceAmount> PriceAmount { get; set; }
        public List<BaseQuantity> BaseQuantity { get; set; }
        public List<AllowanceCharge> AllowanceCharge { get; set; }
    }

    public class PriceAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class RegistrationAddress
    {
        public List<CityName> CityName { get; set; }
        public List<CountrySubentity> CountrySubentity { get; set; }
        public List<Country> Country { get; set; }
    }

    public class RegistrationName
    {
        public string _ { get; set; }
    }

    public class SellersItemIdentification
    {
        public List<ID> ID { get; set; }
    }

    public class StandardItemIdentification
    {
        public List<ID> ID { get; set; }
    }

    public class StartDate
    {
        public string _ { get; set; }
    }

    public class StreetName
    {
        public string _ { get; set; }
    }

    public class Shipment
    {
        public List<ID> ID { get; set; }
        public List<FreightAllowanceCharge> FreightAllowanceCharge { get; set; }
    }

    public class TaxableAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class TaxAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class TaxCategory
    {
        public List<ID> ID { get; set; }
        public List<Percent> Percent { get; set; }
        public List<TaxScheme> TaxScheme { get; set; }
        public List<TaxExemptionReasonCode> TaxExemptionReasonCode { get; set; }
        public List<TaxExemptionReason> TaxExemptionReason { get; set; }
    }

    public class TaxExchangeRate
    {
        public List<CalculationRate> CalculationRate { get; set; }
        public List<DocumentCurrencyCode> SourceCurrencyCode { get; set; }
        public List<DocumentCurrencyCode> TargetCurrencyCode { get; set; }
    }

    public class CalculationRate
    {
        public double _ { get; set; }
    }
    public class TaxExclusiveAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class TaxExemptionReason
    {
        public string _ { get; set; }
    }

    public class TaxExemptionReasonCode
    {
        public string _ { get; set; }
        public string CodeListIdentifier { get; set; }
        public string CodeListAgencyIdentifier { get; set; }
    }

    public class TaxInclusiveAmount
    {
        public double _ { get; set; }
        public string currencyID { get; set; }
    }

    public class TaxPointDate
    {
        public string _ { get; set; }
    }

    public class TaxScheme
    {
        public List<ID> ID { get; set; }
        public List<ItemTaxSchema> TaxTypeCode { get; set; }
    }

    public class TaxSubtotal
    {
        public List<TaxableAmount> TaxableAmount { get; set; }
        public List<TaxAmount> TaxAmount { get; set; }
        public List<Percent> Percent { get; set; }
        public List<TaxCategory> TaxCategory { get; set; }
    }

    public class TaxTotal
    {
        public List<TaxAmount> TaxAmount { get; set; }
        public List<TaxSubtotal> TaxSubtotal { get; set; }
    }

    public class Telefax
    {
        public string _ { get; set; }
    }

    public class Telephone
    {
        public string _ { get; set; }
    }

    public class UBLVersionID
    {
        public string _ { get; set; }
    }

    public class URI
    {
        public string _ { get; set; }
    }

    public class Value
    {
        public string _ { get; set; }
    }
}
