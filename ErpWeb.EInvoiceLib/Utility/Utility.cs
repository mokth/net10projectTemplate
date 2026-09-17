//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace ErpWeb.EInvoiceLib.Utility
//{
//    public class HashUtility
//    {
//        public static string HashString(string text, string salt = "")
//        {
//            if (string.IsNullOrEmpty(text))
//            {
//                return string.Empty;
//            }

//            // Uses SHA256 to create the hash
//            using (var sha = new System.Security.Cryptography.SHA256Managed())
//            {
//                // Convert the string to a byte array first, to be processed
//                byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text + salt);
//                byte[] hashBytes = sha.ComputeHash(textBytes);

//                // Convert back to a string, removing the '-' that BitConverter adds
//                string hash = BitConverter
//                    .ToString(hashBytes)
//                    .Replace("-", String.Empty);

//                return hash;
//            }
//        }

//        public static string StringToBase64(string Base64String)
//        {

//            // Convert string to Base64
//            byte[] bytes = Encoding.UTF8.GetBytes(Base64String);

//            return System.Convert.ToBase64String(bytes, Base64FormattingOptions.None);
//        }
//    }
//}
